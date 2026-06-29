using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Data
{
    public class AppDbContext : DbContext
    {
        public DbSet<User> Users => Set<User>();
        public DbSet<Device> Devices => Set<Device>();
        public DbSet<App> Apps => Set<App>();
        public DbSet<AppUsage> AppUsages => Set<AppUsage>();
        public DbSet<AppIcon> AppIcons => Set<AppIcon>();
        public DbSet<InputEvent> InputEvents => Set<InputEvent>();

        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasIndex(e => e.Username)
                    .IsUnique();
            });

            modelBuilder.Entity<Device>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasIndex(e => new { e.OwnerId, e.HardwareId })
                    .IsUnique();
            });

            modelBuilder.Entity<App>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasIndex(e => e.Name)
                    .IsUnique();
            });

            modelBuilder.Entity<AppUsage>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasOne(e => e.Device)
                    .WithMany()
                    .HasForeignKey(e => e.DeviceId);

                entity.HasOne(e => e.App)
                    .WithMany()
                    .HasForeignKey(e => e.AppId);

                entity.HasIndex(e => e.DeviceId);
                entity.HasIndex(e => e.StartTime);

                // 复合索引：用于合并查询时快速查找同设备+同应用的最新记录
                entity.HasIndex(e => new { e.DeviceId, e.AppId, e.EndTime });
            });

            modelBuilder.Entity<AppIcon>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasOne(e => e.App)
                    .WithMany()
                    .HasForeignKey(e => e.AppId);

                entity.HasIndex(e => e.AppId)
                    .IsUnique();
            });

            modelBuilder.Entity<InputEvent>(entity =>
            {
                // Id 为客户端生成的 UUIDv7，兼作去重键（上传幂等）。
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedNever();

                // 枚举以 short 落库。
                entity.Property(e => e.EventType)
                    .HasConversion<short>();

                entity.HasOne(e => e.Device)
                    .WithMany()
                    .HasForeignKey(e => e.DeviceId);

                // 计数查询走 (DeviceId, Timestamp)。
                entity.HasIndex(e => new { e.DeviceId, e.Timestamp });
            });
        }
    }
}
