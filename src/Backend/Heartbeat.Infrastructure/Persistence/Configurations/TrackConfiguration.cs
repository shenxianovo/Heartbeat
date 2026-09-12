using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Heartbeat.Persistence.Configurations;

internal sealed class TrackConfiguration : IEntityTypeConfiguration<Track>
{
    public void Configure(EntityTypeBuilder<Track> builder)
    {
        builder.ToTable(
            "tracks",
            table =>
            {
                table.HasCheckConstraint("ck_tracks_type", "btrim(type) <> ''");
                table.HasCheckConstraint("ck_tracks_version", "version > 0");
                table.HasCheckConstraint(
                    "ck_tracks_time_mode",
                    "(time_mode = 'point' AND end_mode IS NULL) OR "
                    + "(time_mode = 'range' AND end_mode IS NOT NULL AND end_mode IN ('explicit', 'next_record'))");
            });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.CollectorId).HasColumnName("collector_id").IsRequired();
        builder.Property(x => x.Type).HasColumnName("type").HasColumnType("text").IsRequired();
        builder.Property(x => x.Version).HasColumnName("version").IsRequired();
        builder.Property(x => x.TimeMode)
            .HasColumnName("time_mode")
            .HasColumnType("text")
            .HasConversion<TimeModeConverter>()
            .IsRequired();
        builder.Property(x => x.EndMode)
            .HasColumnName("end_mode")
            .HasColumnType("text")
            .HasConversion<EndModeConverter>();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(x => new { x.CollectorId, x.Type, x.Version }).IsUnique();

        builder
            .HasOne<Collector>()
            .WithMany()
            .HasForeignKey(x => x.CollectorId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
