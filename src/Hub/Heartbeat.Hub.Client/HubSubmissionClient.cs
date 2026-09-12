using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Hub;

public sealed class HubSubmissionClient(HttpClient httpClient)
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
        response.EnsureSuccessStatusCode();

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

    private static bool Confirms(DateTimeOffset? sent, DateTimeOffset? accepted) =>
        sent is null ? accepted is null : accepted is not null && accepted >= sent;
}
