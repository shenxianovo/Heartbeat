using Microsoft.Data.Sqlite;

namespace Heartbeat.Hub;

/// <summary>The same upload/retry loop is composed by desktop and HTTP hosts.</summary>
public sealed class HubDeliveryLoop(RecordUploader uploader, TimeSpan interval)
{
    private string? _lastError;
    public string? LastError => Volatile.Read(ref _lastError);
    public event Action<string>? Failed;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var errors = await uploader.UploadOnceAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _lastError, errors.Count == 0 ? null : string.Join("\n", errors));
            }
            catch (Exception exception) when (exception is SqliteException or IOException)
            {
                Volatile.Write(ref _lastError, "Queue access failed; records remain unconfirmed.");
            }

            if (LastError is { } error) Failed?.Invoke(error);
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }
}
