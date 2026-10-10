using System.Text.Json;
using Heartbeat.Application.Entities;
using Heartbeat.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Integration.Tests;

internal sealed class HeartbeatApiFactory(string connectionString)
    : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Heartbeat"] = connectionString,
            }));

        return base.CreateHost(builder);
    }
}
