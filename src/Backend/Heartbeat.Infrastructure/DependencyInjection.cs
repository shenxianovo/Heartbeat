using Heartbeat.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Heartbeat.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddHeartbeatInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddDbContext<HeartbeatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));

        return services;
    }
}
