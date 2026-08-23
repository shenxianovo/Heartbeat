using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Collector.System.Collection;

public static class SystemCollectorServiceCollectionExtensions
{
    public static IServiceCollection AddSystemCollectorInProcessBinding(
        this IServiceCollection services,
        SystemCollectorBindingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton<SystemCollectorProtocolAdapter>();
        services.AddSingleton<ISystemSegmentPublisher>(provider =>
            provider.GetRequiredService<SystemCollectorProtocolAdapter>());
        services.AddSingleton<AppMonitorService>();
        services.AddSingleton<SystemInProcessCollector>();
        services.AddHostedService<SystemCollectorHostedService>();
        return services;
    }
}
