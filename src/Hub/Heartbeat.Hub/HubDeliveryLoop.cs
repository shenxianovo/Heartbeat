using Microsoft.Data.Sqlite;

namespace Heartbeat.Hub;

/// <summary>The same upload/retry loop is composed by desktop and HTTP hosts.</summary>
public sealed class HubDeliveryLoop(RecordOutbox queue, RecordUploader uploader)
{
    private string? _lastError;
    public string? LastError => Volatile.Read(ref _lastError);
    public event Action<string>? Failed;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var retrySeconds = 1;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var errors = await uploader.UploadOnceAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _lastError, errors.Count == 0 ? null : string.Join("\n", errors));
                if (errors.Count == 0)
                {
                    retrySeconds = 1;
                    await queue.WaitForPendingAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            catch (Exception exception) when (exception is SqliteException or IOException)
            {
                Volatile.Write(ref _lastError, "Queue access failed; records remain unconfirmed.");
            }

            if (LastError is { } error) Failed?.Invoke(error);
            // New submissions must not bypass backoff while the backend is unavailable.
            await Task.Delay(TimeSpan.FromSeconds(retrySeconds), cancellationToken).ConfigureAwait(false);
            retrySeconds = Math.Min(30, retrySeconds * 2);
        }
    }
}
