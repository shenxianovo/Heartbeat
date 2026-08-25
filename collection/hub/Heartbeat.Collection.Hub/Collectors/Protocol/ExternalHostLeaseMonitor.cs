using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Collection.Hub.Collectors.Protocol;

public sealed class ExternalHostLeaseMonitor(
    IServiceProvider services,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var handler = services.GetRequiredService<BrowserExternalHostProtocolHandler>();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            handler.ExpireLeases();
    }
}
