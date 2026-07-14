using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Data
{
    public class AppDbContext : DbContext
    {
        public DbSet<User> Users => Set<User>();
        public DbSet<Device> Devices => Set<Device>();
        public DbSet<App> Apps => Set<App>();
        public DbSet<ActivitySegment> ActivitySegments => Set<ActivitySegment>();
        public DbSet<AppIcon> AppIcons => Set<AppIcon>();
        public DbSet<InputEvent> InputEvents => Set<InputEvent>();
        public DbSet<Recap> Recaps => Set<Recap>();

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

            modelBuilder.Entity<ActivitySegment>(entity =>
            {
                // Id 为采集端生成的 UUIDv7，兼作去重键（幂等重传，ADR-017）。
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedNever();

                entity.Property(e => e.Source).HasMaxLength(64);

                entity.Property(e => e.Attributes).HasColumnType("jsonb");

                entity.HasOne(e => e.Device)
                    .WithMany()
                    .HasForeignKey(e => e.DeviceId);

                entity.HasOne(e => e.App)
                    .WithMany()
                    .HasForeignKey(e => e.AppId);

                entity.HasIndex(e => e.DeviceId);
                entity.HasIndex(e => e.StartTime);

                // 复合索引：ADR-017 的续接查询已随 ADR-018 退役（摄入走 PK upsert）；
                // 保留用于回放/查询按 (Source, IdentityKey) 过滤分组。
                entity.HasIndex(e => new { e.DeviceId, e.Source, e.IdentityKey, e.EndTime });
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
            modelBuilder.Entity<Recap>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Model).HasMaxLength(128);
                entity.Property(e => e.PromptHash).HasMaxLength(16);

                // 缓存身份：一个 Owner 的一个日窗口一份（ADR-023 §4）。
                entity.HasIndex(e => new { e.OwnerId, e.WindowStart })
                    .IsUnique();
            });
        }
    }
}
