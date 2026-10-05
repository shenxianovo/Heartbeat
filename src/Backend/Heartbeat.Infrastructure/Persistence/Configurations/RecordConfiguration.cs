using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Heartbeat.Persistence.Configurations;

internal sealed class RecordConfiguration : IEntityTypeConfiguration<Record>
{
    public void Configure(EntityTypeBuilder<Record> builder)
    {
        builder.ToTable(
            "records",
            table => table.HasCheckConstraint(
                "ck_records_time_range",
                "ended_at IS NULL OR ended_at >= started_at"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.HasOne(x => x.Identity).WithOne().HasForeignKey<Record>(x => x.Id)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.TrackId).HasColumnName("track_id").IsRequired();
        builder.Property(x => x.StartedAt).HasColumnName("started_at").IsRequired();
        builder.Property(x => x.EndedAt).HasColumnName("ended_at");
        builder.Property(x => x.ObservedAt).HasColumnName("observed_at");
        builder.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
        builder.Property(x => x.Objects).HasColumnName("objects").HasColumnType("jsonb")
            .HasConversion<ObjectReferencesConverter>().IsRequired()
            .Metadata.SetValueComparer(new ObjectReferencesComparer());
        builder.Property(x => x.Value).HasColumnName("value").HasColumnType("jsonb").IsRequired();

        builder
            .HasIndex(x => new { x.TrackId, x.StartedAt, x.Id })
            .HasDatabaseName("ix_records_track_time");

        builder
            .HasOne<Track>()
            .WithMany()
            .HasForeignKey(x => x.TrackId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
