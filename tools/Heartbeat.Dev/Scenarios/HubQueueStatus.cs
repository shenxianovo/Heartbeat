using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Dev;

internal sealed record HubQueueStatus([property: JsonRequired] int Pending, [property: JsonRequired] int Failed)
{
    private static readonly JsonSerializerOptions Format = new() { PropertyNameCaseInsensitive = true };

    public static HubQueueStatus Parse(string json) =>
        JsonSerializer.Deserialize<HubQueueStatus>(json, Format)
        ?? throw new InvalidDataException("Missing Hub queue status.");

    public static async Task<HubQueueStatus> ReadAsync(HttpClient client, CancellationToken token) =>
        Parse(await client.GetStringAsync("hub/v1/status", token));

    public static Task WaitReadyAsync(HttpClient client, CancellationToken cancellationToken) =>
        ScenarioWait.UntilAsync("an empty, authenticated Hub", async token =>
        {
            try { return await ReadAsync(client, token) == new HubQueueStatus(0, 0); }
            catch (HttpRequestException exception) when (exception.StatusCode is null) { return false; }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { return false; }
        }, cancellationToken);

    public void RequireAccepted()
    {
        if (Pending <= 0 || Failed != 0)
            throw new InvalidOperationException("No retryable Hub custody was confirmed while the API was offline.");
    }

    public bool IsDrained()
    {
        if (Failed != 0) throw new InvalidOperationException("Hub delivery paused one or more Records; inspect the isolated Hub failures endpoint.");
        return Pending == 0;
    }
}
