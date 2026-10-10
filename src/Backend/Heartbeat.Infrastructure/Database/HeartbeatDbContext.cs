using Heartbeat.Core;
using Heartbeat.Infrastructure.Database.Configurations;
using Heartbeat.Infrastructure.Database.Converters;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Infrastructure.Database;

public sealed class HeartbeatDbContext(
    DbContextOptions<HeartbeatDbContext> options) : DbContext(options)
{
    public DbSet<EntityIndexRow> Entities => Set<EntityIndexRow>();
    public DbSet<Observation> Observations => Set<Observation>();
    public DbSet<Observer> Observers => Set<Observer>();
    public DbSet<EntitySchema> EntitySchemas
        => Set<EntitySchema>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<EntityId>()
            .HaveConversion<EntityIdValueConverter>();

        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UtcDateTimeOffsetValueConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new EntityIndexRowConfiguration());
        modelBuilder.ApplyConfiguration(new ObservationConfiguration());
        modelBuilder.ApplyConfiguration(new ObserverConfiguration());
        modelBuilder.ApplyConfiguration(new EntitySchemaConfiguration());
    }
}
