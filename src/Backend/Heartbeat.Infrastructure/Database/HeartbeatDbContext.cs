using Heartbeat.Core;
using Heartbeat.Infrastructure.Database.Configurations;
using Heartbeat.Infrastructure.Database.Converters;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Infrastructure.Database;

public sealed class HeartbeatDbContext(
    DbContextOptions<HeartbeatDbContext> options) : DbContext(options)
{
    public DbSet<EntityIndexRow> Entities => Set<EntityIndexRow>();
    public DbSet<EntityDataRow> EntityData => Set<EntityDataRow>();
    public DbSet<Observation> Observations => Set<Observation>();
    public DbSet<Observer> Observers => Set<Observer>();
    public DbSet<ObservationSchema> ObservationSchemas
        => Set<ObservationSchema>();

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
        modelBuilder.ApplyConfiguration(new EntityDataRowConfiguration());
        modelBuilder.ApplyConfiguration(new ObservationConfiguration());
        modelBuilder.ApplyConfiguration(new ObserverConfiguration());
        modelBuilder.ApplyConfiguration(new ObservationSchemaConfiguration());
    }
}
