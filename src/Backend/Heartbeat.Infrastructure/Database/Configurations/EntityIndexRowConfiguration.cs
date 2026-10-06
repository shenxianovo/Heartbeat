using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Heartbeat.Infrastructure.Database.Configurations;

public sealed class EntityIndexRowConfiguration : IEntityTypeConfiguration<EntityIndexRow>
{
    public void Configure(EntityTypeBuilder<EntityIndexRow> builder)
    {
        builder.ToTable("entities");
        builder.HasKey(row => row.Id)
            .HasName("pk_entities");

        builder.Property(row => row.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(row => row.TableName)
            .HasColumnName("table_name")
            .HasColumnType("text")
            .IsRequired();
    }
}
