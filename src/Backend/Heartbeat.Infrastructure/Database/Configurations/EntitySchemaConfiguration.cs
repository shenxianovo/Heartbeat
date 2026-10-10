using Heartbeat.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Heartbeat.Infrastructure.Database.Configurations;

public sealed class EntitySchemaConfiguration : IEntityTypeConfiguration<EntitySchema>
{
    public void Configure(EntityTypeBuilder<EntitySchema> builder)
    {
        builder.ToTable("entity_schemas");
        builder.HasKey(schema => schema.Id)
            .HasName("pk_entity_schemas");

        builder.HasOne<EntityIndexRow>()
            .WithOne()
            .HasForeignKey<EntitySchema>(schema => schema.Id)
            .HasConstraintName("fk_entity_schemas_entities_id")
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

        builder.HasIndex(schema => schema.ResourceName).IsUnique().HasDatabaseName("ux_entity_schemas_resource_name");
        builder.Property(schema => schema.ResourceName)
            .HasColumnName("resource_name").HasColumnType("text").IsRequired();

        builder.Property(schema => schema.Fields)
            .HasColumnName("fields")
            .HasColumnType("jsonb")
            .IsRequired();
    }
}
