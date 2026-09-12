using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Heartbeat.Persistence.Configurations;

internal sealed class CollectorConfiguration : IEntityTypeConfiguration<Collector>
{
    public void Configure(EntityTypeBuilder<Collector> builder)
    {
        builder.ToTable(
            "collectors",
            table =>
            {
                table.HasCheckConstraint("ck_collectors_key", "btrim(key) <> ''");
                table.HasCheckConstraint("ck_collectors_target", "btrim(target) <> ''");
                table.HasCheckConstraint("ck_collectors_display_name", "btrim(display_name) <> ''");
            });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.TimelineId).HasColumnName("timeline_id").IsRequired();
        builder.Property(x => x.Key).HasColumnName("key").HasColumnType("text").IsRequired();
        builder.Property(x => x.Target).HasColumnName("target").HasColumnType("text").IsRequired();
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasColumnType("text").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(x => new { x.TimelineId, x.Key, x.Target }).IsUnique();

        builder
            .HasOne<Timeline>()
            .WithMany()
            .HasForeignKey(x => x.TimelineId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
