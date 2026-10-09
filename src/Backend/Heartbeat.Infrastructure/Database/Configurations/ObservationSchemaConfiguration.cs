using Heartbeat.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Heartbeat.Infrastructure.Database.Configurations;

public sealed class ObservationSchemaConfiguration : IEntityTypeConfiguration<ObservationSchema>
{
    public void Configure(EntityTypeBuilder<ObservationSchema> builder)
    {
        builder.ToTable("observation_schemas");
        builder.HasKey(schema => schema.Id)
            .HasName("pk_observation_schemas");

        builder.HasOne<EntityIndexRow>()
            .WithOne()
            .HasForeignKey<ObservationSchema>(schema => schema.Id)
            .HasConstraintName("fk_observation_schemas_entities_id")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(schema => schema.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(schema => schema.Name)
            .HasColumnName("name")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(schema => schema.Schema)
            .HasColumnName("schema")
            .HasColumnType("jsonb")
            .IsRequired();
    }
}
