using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Segments;

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
        services.TryAddSingleton(provider =>
        {
            var bindingOptions = provider.GetRequiredService<SystemCollectorBindingOptions>();
            return CollectorRuntime.Open(
                Path.Combine(bindingOptions.DataDirectory, "collector-runtime.json"),
                provider.GetRequiredService<ISegmentSink>(),
                appHintResolver: provider.GetRequiredService<ICollectorAppHintResolver>());
        });
        services.AddHostedService<SystemCollectorHostedService>();
        return services;
    }
}
