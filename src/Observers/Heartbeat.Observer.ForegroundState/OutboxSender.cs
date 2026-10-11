using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Heartbeat.Observers.ForegroundState;

internal sealed class OutboxSender(ForegroundStore store, HttpClient client, TextWriter progress)
{
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await SynchronizeAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    internal async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        foreach (var period in store.Pending())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var present = true;
                foreach (var entity in period.Entities)
                    present &= await CheckOrSendAsync(entity, cancellationToken);
                if (present)
                {
                    store.Remove(period.Sequence);
                    await progress.WriteLineAsync($"Outbox confirmed {period.ObservationId}; removed locally.");
                }
                else store.RecordError(period.Sequence, null);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException
                || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                store.RecordError(period.Sequence, exception.Message);
                await progress.WriteLineAsync($"Outbox retained {period.ObservationId}: {exception.Message}");
                // A transport outage affects the whole destination; don't wait on every queued period.
                if (exception is OperationCanceledException or HttpRequestException { StatusCode: null }) break;
            }
        }
    }

    private async Task<bool> CheckOrSendAsync(EntitySubmission entity, CancellationToken cancellationToken)
    {
        var readPath = $"entities/{entity.Id}";
        using var read = await client.GetAsync(readPath, cancellationToken);
        if (read.StatusCode == HttpStatusCode.OK)
        {
            var actual = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (!entity.Matches(actual))
                throw new InvalidDataException($"{readPath} exists with different content; local data was retained.");
            return true;
        }
        if (read.StatusCode != HttpStatusCode.NotFound) throw await FailureAsync(read, readPath, cancellationToken);
        var savePath = $"{entity.SavePath}/{entity.Id}";
        using var save = await client.PutAsJsonAsync(savePath, entity.Body, cancellationToken);
        if (save.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.NoContent))
            throw await FailureAsync(save, savePath, cancellationToken);
        // Only a later read removes local data; a successful send isn't used as a receipt.
        return false;
    }

    private static async Task<HttpRequestException> FailureAsync(
        HttpResponseMessage response, string path, CancellationToken cancellationToken) =>
        new($"{path}: HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}",
            null, response.StatusCode);
}
