using Heartbeat.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddHeartbeatInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<HeartbeatDbContext>(options =>
            options.UseNpgsql(connectionString));

        return services;
    }
}
