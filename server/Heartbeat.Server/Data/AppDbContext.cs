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
        public DbSet<Strand> Strands => Set<Strand>();
        public DbSet<StrandMatcher> StrandMatchers => Set<StrandMatcher>();
        public DbSet<MutedMatcher> MutedMatchers => Set<MutedMatcher>();
        public DbSet<DailyQuestionSet> DailyQuestionSets => Set<DailyQuestionSet>();

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

                // 写权按 owner 隔离（ADR-025）：一个 App 每个 owner 一份图标。
                entity.HasIndex(e => new { e.OwnerId, e.AppId })
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

            modelBuilder.Entity<Strand>(entity =>
            {
                // Id 为服务端生成的 UUIDv7。
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedNever();

                entity.Property(e => e.Name).HasMaxLength(256);

                // 无 Id 提交的收敛键：按 (OwnerId, Name) 定位既有行，重复提交幂等不产重复 Strand。
                entity.HasIndex(e => new { e.OwnerId, e.Name })
                    .IsUnique();

                entity.HasMany(e => e.Members)
                    .WithOne(m => m.Strand)
                    .HasForeignKey(m => m.StrandId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<StrandMatcher>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Source).HasMaxLength(64);

                // StepsJson 为规范化序列（MatcherNormalizer + MatcherCodec）：幂等按字符串相等收敛。
                entity.HasIndex(e => new { e.StrandId, e.Source, e.StepsJson })
                    .IsUnique();
            });

            modelBuilder.Entity<MutedMatcher>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Source).HasMaxLength(64);

                entity.HasIndex(e => new { e.OwnerId, e.Source, e.StepsJson })
                    .IsUnique();
            });

            modelBuilder.Entity<DailyQuestionSet>(entity =>
            {
                entity.HasKey(e => e.Id);

                // 缓存身份：一个 Owner 的一个日窗口一份（与 Recap 同构，ADR-029 §4）。
                entity.HasIndex(e => new { e.OwnerId, e.WindowStart })
                    .IsUnique();
            });
        }
    }
}
