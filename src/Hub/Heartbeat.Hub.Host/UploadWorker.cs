namespace Heartbeat.Hub.Host;

internal sealed partial class UploadWorker(RecordUploader uploader, HubSettings settings, ILogger<UploadWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delivery = new HubDeliveryLoop(uploader, settings.UploadInterval);
        delivery.Failed += error => LogUnconfirmed(logger, error);
        await delivery.RunAsync(stoppingToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{UploadError}")]
    private static partial void LogUnconfirmed(ILogger logger, string uploadError);

}
