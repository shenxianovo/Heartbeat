using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Data
{
    public class AppDbContext : DbContext
    {
        public DbSet<User> Users => Set<User>();
        public DbSet<Device> Devices => Set<Device>();
        public DbSet<App> Apps => Set<App>();
        public DbSet<AppIdentity> AppIdentities => Set<AppIdentity>();
        public DbSet<ActivitySegment> ActivitySegments => Set<ActivitySegment>();
        public DbSet<AppIcon> AppIcons => Set<AppIcon>();
        public DbSet<AppMergeReceipt> AppMergeReceipts => Set<AppMergeReceipt>();
        public DbSet<InputEvent> InputEvents => Set<InputEvent>();
        public DbSet<Recap> Recaps => Set<Recap>();
        public DbSet<Strand> Strands => Set<Strand>();
        public DbSet<StrandMatcher> StrandMatchers => Set<StrandMatcher>();
        public DbSet<MutedMatcher> MutedMatchers => Set<MutedMatcher>();
        public DbSet<Episode> Episodes => Set<Episode>();
        public DbSet<RecurrenceProbe> RecurrenceProbes => Set<RecurrenceProbe>();
        public DbSet<DailyQuestionSet> DailyQuestionSets => Set<DailyQuestionSet>();
        public DbSet<CollectorDeclaration> CollectorDeclarations => Set<CollectorDeclaration>();

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

                entity.HasOne(e => e.CurrentAppIdentity)
                    .WithMany()
                    .HasForeignKey(e => e.CurrentAppIdentityId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasIndex(e => e.CurrentAppIdentityId);
            });

            modelBuilder.Entity<App>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Key).HasMaxLength(256);
                entity.Property(e => e.DisplayName).HasMaxLength(256);

                entity.HasIndex(e => e.Key)
                    .IsUnique();
            });

            modelBuilder.Entity<AppIdentity>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Key).HasMaxLength(512);
                entity.HasIndex(e => e.Key).IsUnique();

                entity.HasOne(e => e.App)
                    .WithMany(e => e.Identities)
                    .HasForeignKey(e => e.AppId)
                    .OnDelete(DeleteBehavior.Restrict);
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

                entity.HasOne(e => e.AppIdentity)
                    .WithMany()
                    .HasForeignKey(e => e.AppIdentityId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasIndex(e => e.DeviceId);
                entity.HasIndex(e => e.AppIdentityId);
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

            modelBuilder.Entity<AppMergeReceipt>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.SourceAppKey).HasMaxLength(256);
                entity.Property(e => e.TargetAppKey).HasMaxLength(256);
                entity.Property(e => e.ResponseJson).HasColumnType("jsonb");
                entity.HasIndex(e => new { e.SourceAppKey, e.TargetAppKey }).IsUnique();
            });

            modelBuilder.Entity<InputEvent>(entity =>
            {
                // Id 为客户端生成的 UUIDv7，兼作去重键（上传幂等）。
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedNever();

                // 枚举以 short 落库。
                entity.Property(e => e.EventType)
                    .HasConversion<short>();
                entity.Property(e => e.CodeSet)
                    .HasMaxLength(64);

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
                entity.Property(e => e.KnowledgeHash).HasMaxLength(64);

                // 缓存身份：一个 Owner 的一个日窗口一份（ADR-023 §4）。
                entity.HasIndex(e => new { e.OwnerId, e.WindowStart })
                    .IsUnique();
            });

            modelBuilder.Entity<Strand>(entity =>
            {
                // Id 为应用层生成的 UUIDv7（ADR-031 §1）。
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedNever();

                entity.Property(e => e.Name).HasMaxLength(256);
                entity.Property(e => e.NormalizedName).HasMaxLength(256);

                // 严格单父级树（ADR-031 §2）：自引用 FK，无环由服务层校验（数据库表达不了）。
                // 不级联删除——没有删除子树的领域操作。
                entity.HasOne(e => e.Parent)
                    .WithMany()
                    .HasForeignKey(e => e.ParentStrandId)
                    .OnDelete(DeleteBehavior.Restrict);

                // 同 Owner、同父、同规范名允许多行（不同时期），日期范围不重叠由服务层校验——
                // 旧 (OwnerId, lower(Name)) 唯一索引随按名收敛语义退役（ADR-031 迁移）。
                entity.HasIndex(e => new { e.OwnerId, e.ParentStrandId, e.NormalizedName });

                // 陈旧提案不覆盖新编辑（ADR-031 §6）：UPDATE 带 WHERE Version = 读取值。
                entity.Property(e => e.Version).IsConcurrencyToken();

                entity.HasMany(e => e.Members)
                    .WithOne(m => m.Strand)
                    .HasForeignKey(m => m.StrandId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<StrandMatcher>(entity =>
            {
                // Id 为应用层生成的 UUIDv7（ADR-031 §1）；业务身份仍是 canonical 谓词。
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedNever();

                entity.Property(e => e.Source).HasMaxLength(64);

                // StepsJson 为规范化序列（MatcherNormalizer + MatcherCodec）：幂等按字符串相等收敛。
                entity.HasIndex(e => new { e.StrandId, e.Source, e.StepsJson })
                    .IsUnique();
            });

            modelBuilder.Entity<MutedMatcher>(entity =>
            {
                // Id 为应用层生成的 UUIDv7（ADR-031 §1）。
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedNever();

                entity.Property(e => e.Source).HasMaxLength(64);

                entity.HasIndex(e => new { e.OwnerId, e.Source, e.StepsJson })
                    .IsUnique();
            });

            modelBuilder.Entity<Episode>(entity =>
            {
                // Id 为应用层生成的 UUIDv7（ADR-031 §1）。
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedNever();

                // 至多一个最具体 Strand（ADR-031 §4）：不级联——Strand 没有删除操作，
                // 解除关联是显式的领域写。
                entity.HasOne(e => e.RelatedStrand)
                    .WithMany()
                    .HasForeignKey(e => e.RelatedStrandId)
                    .OnDelete(DeleteBehavior.Restrict);

                // 按日期与按 Strand 浏览的读取键（Owner 隔离）。
                entity.HasIndex(e => new { e.OwnerId, e.LocalDate });
                entity.HasIndex(e => e.RelatedStrandId);

                // 陈旧提案不覆盖新编辑（ADR-031 §6）。
                entity.Property(e => e.Version).IsConcurrencyToken();
            });

            modelBuilder.Entity<RecurrenceProbe>(entity =>
            {
                // Id 为应用层生成的 UUIDv7（ADR-031 §1）；业务身份是 (Episode, canonical 谓词)。
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedNever();

                entity.Property(e => e.Source).HasMaxLength(64);
                entity.Property(e => e.Status).HasMaxLength(16);

                entity.HasOne(e => e.Episode)
                    .WithMany(ep => ep.Probes)
                    .HasForeignKey(e => e.EpisodeId)
                    .OnDelete(DeleteBehavior.Cascade);

                // 同一 Episode 的同一 canonical 谓词只有一行——含已解决行：
                // 解决结果"钉住"该谓词，不允许再建活跃 Probe 重复发问（ADR-031 §5）。
                entity.HasIndex(e => new { e.EpisodeId, e.Source, e.StepsJson })
                    .IsUnique();

                // Asking 侧扫活跃 Probe 的读取键。
                entity.HasIndex(e => new { e.OwnerId, e.Status });
            });

            modelBuilder.Entity<DailyQuestionSet>(entity =>
            {
                entity.HasKey(e => e.Id);

                // 缓存身份：一个 Owner 的一个日窗口一份（与 Recap 同构，ADR-029 §4）。
                entity.HasIndex(e => new { e.OwnerId, e.WindowStart })
                    .IsUnique();
            });

            modelBuilder.Entity<CollectorDeclaration>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Source).HasMaxLength(64);
                entity.Property(e => e.PayloadJson).HasColumnType("jsonb");

                // 生效规则的读取键：每 Source 取 max(Version)；同 (Source, Version) 幂等覆盖（ADR-030 §4）。
                entity.HasIndex(e => new { e.Source, e.Version })
                    .IsUnique();
            });
        }
    }
}
