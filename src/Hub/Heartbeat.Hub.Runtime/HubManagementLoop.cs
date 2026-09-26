using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Management;

namespace Heartbeat.Hub.Runtime;

public sealed class HubManagementLoop(HttpClient http, IBackendTokenProvider tokens, DeliveryDestination destination,
    Guid hubId, Func<HubReport> report, ICollectorManager collectors, DeliveryActivity activity)
{
    private readonly Guid _sessionId = Guid.NewGuid();
    public string? LastError { get; private set; }

    public Task RunAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(RunManagementAsync(cancellationToken), RunActivityAsync(cancellationToken));

    private async Task RunManagementAsync(CancellationToken cancellationToken)
    {
        HubCommandResult? result = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var command = await CheckInAsync(result, cancellationToken).ConfigureAwait(false);
                result = null;
                LastError = null;
                if (command is not null)
                {
                    result = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                    continue; // Acknowledge immediately, including the updated actual state.
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                exception is HttpRequestException or OperationCanceledException or JsonException or IOException)
            {
                LastError = "Hub management connection unavailable; local collection and delivery continue.";
            }
            await Task.Delay(HubManagement.CheckInInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunActivityAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var request = await AuthorizedRequestAsync($"api/v1/hubs/{hubId}/activity", token);
                request.Content = JsonContent.Create(new HubActivityReport(_sessionId, activity.Snapshot));
                using var response = await http.SendAsync(request, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized) tokens.Invalidate();
                // No telemetry replay queue, no influence on Record delivery or management availability.
            }
            catch (Exception exception) when (!token.IsCancellationRequested &&
                exception is HttpRequestException or OperationCanceledException or IOException) { }
            await Task.Delay(HubManagement.ActivityInterval, token).ConfigureAwait(false);
        }
    }

    private async Task<HttpRequestMessage> AuthorizedRequestAsync(string path, CancellationToken token)
    {
        var access = await tokens.GetTokenAsync(token).ConfigureAwait(false);
        if (access is null || access.OwnerId != destination.OwnerId) throw new IOException("Owner authentication failed.");
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(destination.BackendUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Value);
        return request;
    }

    private async Task<HubCommand?> CheckInAsync(HubCommandResult? result, CancellationToken token)
    {
        using var request = await AuthorizedRequestAsync($"api/v1/hubs/{hubId}/check-in", token);
        request.Content = JsonContent.Create(new HubCheckIn(_sessionId, report(), result));
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized) tokens.Invalidate();
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<HubCheckInResponse>(token).ConfigureAwait(false)
            ?? throw new IOException("Missing management response.")).Command;
    }

    private async Task<HubCommandResult> ExecuteAsync(HubCommand command, CancellationToken token)
    {
        if (command.ExpiresAt <= DateTimeOffset.UtcNow) return new(command.Id, false, "Operation expired before execution.");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            var remaining = command.ExpiresAt - DateTimeOffset.UtcNow;
            deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            await collectors.ExecuteAsync(command.Operation, deadline.Token).ConfigureAwait(false);
            return new(command.Id, true);
        }
        catch (ArgumentException exception) { return new(command.Id, false, exception.Message); }
        catch (InvalidOperationException exception) { return new(command.Id, false, exception.Message); }
        catch (Exception) when (!token.IsCancellationRequested)
        { return new(command.Id, false, "Collector operation failed. Inspect its status and local configuration."); }
    }
}
