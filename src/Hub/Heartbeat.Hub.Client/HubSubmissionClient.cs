using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Hub;

public interface IHubSubmissionClient
{
    Task SubmitAsync(HubSubmission submission, CancellationToken cancellationToken = default);
}

public sealed class HubSubmissionClient(HttpClient httpClient) : IHubSubmissionClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task SubmitAsync(HubSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        using var response = await httpClient.PostAsJsonAsync(
            "/hub/v1/records", submission, JsonOptions, cancellationToken);
        await EnsureAcceptedAsync(response, cancellationToken);

        var body = await response.Content.ReadFromJsonAsync<HubSubmissionResponse>(JsonOptions, cancellationToken);
        if (body?.Results is null || submission.Records is null || body.Results.Count != submission.Records.Count)
        {
            throw new InvalidDataException("Hub returned incomplete custody receipts.");
        }

        var seen = new HashSet<int>();
        foreach (var receipt in body.Results)
        {
            if (receipt?.Index is not { } index || index < 0 || index >= submission.Records.Count || !seen.Add(index) ||
                submission.Records[index] is not { } record || receipt.Id != record.Id || receipt.Status != "accepted" ||
                !Confirms(record.EndedAt, receipt.EndedAt))
            {
                throw new InvalidDataException("A Hub receipt does not confirm its submitted Record snapshot.");
            }
        }
    }

    private static async Task EnsureAcceptedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest &&
            response.Content.Headers.ContentType?.MediaType == "application/problem+json")
        {
            using var problem = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
            if (problem?.RootElement.TryGetProperty("detail", out var detail) == true &&
                detail.ValueKind == JsonValueKind.String)
            {
                throw new HttpRequestException(
                    $"Hub rejected submission (400): {detail.GetString()}", null, response.StatusCode);
            }
        }
        response.EnsureSuccessStatusCode();
    }

    private static bool Confirms(DateTimeOffset? sent, DateTimeOffset? accepted) =>
        sent is null ? accepted is null : accepted is not null && accepted >= sent;
}
