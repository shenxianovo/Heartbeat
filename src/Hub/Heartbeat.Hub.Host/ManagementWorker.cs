using Heartbeat.Hub.Runtime;
using Heartbeat.Management;

namespace Heartbeat.Hub.Host;

internal sealed class ManagementWorker(CollectorManager collectors, HubLocalStorage storage, HubSettings settings,
    RecordOutbox queue, HubDeliveryLoop delivery, HttpClient http, IBackendTokenProvider tokens) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await collectors.RestoreAsync(stoppingToken);
        var loop = new HubManagementLoop(http, tokens, settings.Destination, storage.Id,
            () => new HubReport(Environment.MachineName, "server", collectors.Types, collectors.Collectors,
                new DeliveryState(queue.Status().Pending, queue.Status().Failed, delivery.LastError)), collectors);
        await loop.RunAsync(stoppingToken);
    }
}
