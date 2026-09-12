using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Persistence;

public sealed class HeartbeatDbContext(DbContextOptions<HeartbeatDbContext> options) : DbContext(options)
{
    public DbSet<Timeline> Timelines => Set<Timeline>();

    public DbSet<Collector> Collectors => Set<Collector>();

    public DbSet<Track> Tracks => Set<Track>();

    public DbSet<Record> Records => Set<Record>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(HeartbeatDbContext).Assembly);
    }
}
