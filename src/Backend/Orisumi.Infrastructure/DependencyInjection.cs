using Orisumi.Application.Management;
using Orisumi.Infrastructure.Database;
using Orisumi.Application.Entities;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Orisumi.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOrisumiInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddDbContext<OrisumiDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IEntityStore, PostgresEntityStore>();
        services.AddScoped<EntityService>();
        services.AddSingleton<IManagementStore, PostgresManagementStore>();
        services.AddSingleton<ObservationPreviewCache>();
        services.AddScoped<ObservationPreviewService>();
        return services;
    }
}
