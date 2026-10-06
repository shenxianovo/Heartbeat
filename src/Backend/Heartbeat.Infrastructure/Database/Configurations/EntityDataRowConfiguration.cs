using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Heartbeat.Infrastructure.Database.Configurations;

public sealed class EntityDataRowConfiguration : IEntityTypeConfiguration<EntityDataRow>
{
    public void Configure(EntityTypeBuilder<EntityDataRow> builder)
    {
        builder.ToTable("entity_data");
        builder.HasKey(row => row.Id)
            .HasName("pk_entity_data");

        builder.HasOne<EntityIndexRow>()
            .WithOne()
            .HasForeignKey<EntityDataRow>(row => row.Id)
            .HasConstraintName("fk_entity_data_entities_id")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(row => row.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(row => row.Data)
            .HasColumnName("data")
            .HasColumnType("jsonb")
            .IsRequired();
    }
}
