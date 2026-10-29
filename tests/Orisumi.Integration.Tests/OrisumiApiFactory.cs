using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orisumi.Integration.Tests;

internal sealed class OrisumiApiFactory(string connectionString,
    IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? configureServices = null)
    : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Orisumi"] = connectionString,
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = null,
                ["OPENAI_API_KEY"] = null,
            });
            if (settings is not null)
            {
                configuration.AddInMemoryCollection(settings);
            }
        });

        if (configureServices is not null) builder.ConfigureServices(configureServices);
        return base.CreateHost(builder);
    }
}
