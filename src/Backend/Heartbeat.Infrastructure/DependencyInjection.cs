using Heartbeat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Heartbeat")
            ?? throw new InvalidOperationException("Connection string 'Heartbeat' is not configured.");

        services.AddDbContext<HeartbeatDbContext>(options =>
            options.UseNpgsql(connectionString));

        return services;
    }

    public static async Task MigrateDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HeartbeatDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
