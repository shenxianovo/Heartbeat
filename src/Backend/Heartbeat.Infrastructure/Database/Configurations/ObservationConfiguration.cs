using Heartbeat.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Heartbeat.Infrastructure.Database.Configurations;

public sealed class ObservationConfiguration : IEntityTypeConfiguration<Observation>
{
    public void Configure(EntityTypeBuilder<Observation> builder)
    {
        builder.ToTable("observations", table =>
            table.HasCheckConstraint("ck_observations_time_order", "start_at <= end_at"));
        builder.HasKey(observation => observation.Id)
            .HasName("pk_observations");

        builder.HasOne<EntityIndexRow>()
            .WithOne()
            .HasForeignKey<Observation>(observation => observation.Id)
            .HasConstraintName("fk_observations_entities_id")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(observation => observation.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(observation => observation.ObserverId)
            .HasColumnName("observer_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(observation => observation.ContentId)
            .HasColumnName("content_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(observation => observation.SchemaId)
            .HasColumnName("schema_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(observation => observation.StartAt)
            .HasColumnName("start_at")
            .HasColumnType("timestamptz")
            .IsRequired(false);

        builder.Property(observation => observation.EndAt)
            .HasColumnName("end_at")
            .HasColumnType("timestamptz")
            .IsRequired(false);

        builder.Property(observation => observation.TimeZone)
            .HasColumnName("time_zone")
            .HasColumnType("text")
            .IsRequired(false);
    }
}
