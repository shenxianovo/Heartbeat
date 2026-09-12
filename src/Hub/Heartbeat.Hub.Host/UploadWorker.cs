using Microsoft.Data.Sqlite;

namespace Heartbeat.Hub.Host;

internal sealed partial class UploadWorker(RecordUploader uploader, HubSettings settings, ILogger<UploadWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var error in await uploader.UploadOnceAsync(stoppingToken))
                {
                    LogUnconfirmed(logger, error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is SqliteException or IOException)
            {
                LogQueueFailure(logger, exception);
            }

            await Task.Delay(settings.UploadInterval, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{UploadError}")]
    private static partial void LogUnconfirmed(ILogger logger, string uploadError);

    [LoggerMessage(Level = LogLevel.Error, Message = "Queue access failed; records remain unconfirmed.")]
    private static partial void LogQueueFailure(ILogger logger, Exception exception);
}
