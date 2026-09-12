using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Heartbeat.Hub;

public sealed class RecordUploader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RecordOutbox _queue;
    private readonly HttpClient _httpClient;
    private readonly string _backendToken;

    public RecordUploader(RecordOutbox queue, HttpClient httpClient, string backendToken)
    {
        queue.Destination.CheckTokenOwner(backendToken);
        _queue = queue;
        _httpClient = httpClient;
        _backendToken = backendToken;
    }

    public async Task<IReadOnlyList<string>> UploadOnceAsync(CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var pending = _queue.TakePending();
        foreach (var routeRecords in pending.GroupBy(record => RouteKey(record.Route)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guid trackId;
            try
            {
                trackId = await ResolveTrackAsync(routeRecords.First().Route, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsUnconfirmed(exception))
            {
                errors.Add($"Route {routeRecords.Key}: mapping unconfirmed ({Reason(exception)}); retaining records.");
                continue;
            }

            foreach (var sent in SplitBatches(routeRecords))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var request = Request(HttpMethod.Post, $"api/v1/tracks/{trackId}/records");
                    request.Content = JsonContent.Create(new { records = sent.Select(item => item.Record).ToArray() },
                        options: JsonOptions);
                    using var response = await _httpClient.SendAsync(request, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        var failure = await PermanentFailureAsync(response, cancellationToken);
                        if (failure is not null)
                        {
                            _queue.Apply(sent.Select(item => new DeliveryOutcome(item, false, failure)).ToArray());
                            continue;
                        }

                        response.EnsureSuccessStatusCode();
                    }

                    var body = await response.Content.ReadFromJsonAsync<UploadResponse>(JsonOptions, cancellationToken);
                    _queue.Apply(ValidateReceipts(body, sent));
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsUnconfirmed(exception))
                {
                    errors.Add($"Track {trackId}: upload unconfirmed ({Reason(exception)}); retaining records.");
                }
            }
        }

        return errors;
    }

    private async Task<Guid> ResolveTrackAsync(DeliveryRoute route, CancellationToken cancellationToken)
    {
        if (route.BackendTrackId is { } mapped)
        {
            return mapped;
        }

        using var register = Request(HttpMethod.Post, "api/v1/collectors");
        register.Content = JsonContent.Create(route.Collector, options: JsonOptions);
        using var registrationResponse = await _httpClient.SendAsync(register, cancellationToken);
        registrationResponse.EnsureSuccessStatusCode();
        var collector = await registrationResponse.Content.ReadFromJsonAsync<CollectorResponse>(JsonOptions, cancellationToken);
        if (collector is null || collector.Id == Guid.Empty || collector.Key != route.Collector.Key ||
            collector.Target != route.Collector.Target)
        {
            throw new InvalidDataException("Collector registration returned a mismatched identity.");
        }

        using var resolve = Request(HttpMethod.Post, $"api/v1/collectors/{collector.Id}/tracks");
        resolve.Content = JsonContent.Create(route.Track, options: JsonOptions);
        using var trackResponse = await _httpClient.SendAsync(resolve, cancellationToken);
        trackResponse.EnsureSuccessStatusCode();
        var track = await trackResponse.Content.ReadFromJsonAsync<TrackResponse>(JsonOptions, cancellationToken);
        if (track is null || track.Id == Guid.Empty || track.CollectorId != collector.Id ||
            track.Type != route.Track.Type || track.Version != route.Track.Version ||
            track.TimeMode != route.Track.TimeMode || track.EndMode != route.Track.EndMode)
        {
            throw new InvalidDataException("Track resolution returned a mismatched identity or time definition.");
        }

        _queue.SaveMapping(route, collector.Id, track.Id);
        return track.Id;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_queue.Destination.BackendUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _backendToken);
        return request;
    }

    private static string RouteKey(DeliveryRoute route) => $"local:{route.Id}";

    private static bool IsUnconfirmed(Exception exception) =>
        exception is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException;

    private static string Reason(Exception exception) =>
        exception is HttpRequestException { StatusCode: { } status }
            ? $"HTTP {(int)status}" : exception.GetType().Name;

    private static IEnumerable<PendingRecord[]> SplitBatches(IEnumerable<PendingRecord> records)
    {
        var batch = new List<PendingRecord>();
        var bytes = 14;
        foreach (var record in records)
        {
            var size = RecordOutbox.EncodedSize(record.Record);
            if (batch.Count > 0 && bytes + size + 1 > RecordOutbox.MaximumBatchBytes)
            {
                yield return [.. batch];
                batch.Clear();
                bytes = 14;
            }

            bytes += size + (batch.Count == 0 ? 0 : 1);
            batch.Add(record);
        }

        if (batch.Count > 0)
        {
            yield return [.. batch];
        }
    }

    private static DeliveryOutcome[] ValidateReceipts(UploadResponse? response, PendingRecord[] sent)
    {
        if (response?.Results is null || response.Results.Count != sent.Length)
        {
            throw new InvalidDataException("Missing per-record receipts.");
        }

        var seen = new HashSet<int>();
        var outcomes = new List<DeliveryOutcome>(sent.Length);
        foreach (var result in response.Results)
        {
            if (result?.Index is not { } index || index < 0 || index >= sent.Length || !seen.Add(index) ||
                result.Id != sent[index].Record.Id)
            {
                throw new InvalidDataException("A receipt does not identify its submitted snapshot.");
            }

            switch (result.Status)
            {
                case "stored" when result.ReceivedAt is not null && Confirms(sent[index].Record.EndedAt, result.EndedAt):
                    outcomes.Add(new DeliveryOutcome(sent[index], true));
                    break;
                case "invalid_record" or "conflict" or "track_not_found":
                    outcomes.Add(new DeliveryOutcome(sent[index], false, result.Status));
                    break;
                default:
                    throw new InvalidDataException("Unknown or incomplete Record receipt.");
            }
        }

        return [.. outcomes];
    }

    private static bool Confirms(DateTimeOffset? sent, DateTimeOffset? stored) =>
        sent is null ? stored is null : stored is not null && DatabaseMicroseconds(stored.Value) >= DatabaseMicroseconds(sent.Value);

    private static long DatabaseMicroseconds(DateTimeOffset value) =>
        (value.UtcTicks - 630822816000000000L) / 10;

    private static async Task<string?> PermanentFailureAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.NotFound))
        {
            return null;
        }

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (body.RootElement.ValueKind != JsonValueKind.Object ||
            !body.RootElement.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return (response.StatusCode, code.GetString()) switch
        {
            (HttpStatusCode.BadRequest, "invalid_request") => code.GetString(),
            (HttpStatusCode.NotFound, "track_not_found") => code.GetString(),
            _ => null,
        };
    }

    private sealed record CollectorResponse(Guid Id, string? Key, string? Target);

    private sealed record TrackResponse(Guid Id, Guid CollectorId, string? Type, int Version,
        string? TimeMode, string? EndMode);

    private sealed record UploadResponse(IReadOnlyList<UploadReceipt?>? Results);

    private sealed record UploadReceipt(int? Index, Guid? Id, string? Status,
        DateTimeOffset? EndedAt, DateTimeOffset? ReceivedAt);
}
