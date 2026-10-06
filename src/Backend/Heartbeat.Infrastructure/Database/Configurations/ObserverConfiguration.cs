using Heartbeat.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Heartbeat.Infrastructure.Database.Configurations;

public sealed class ObserverConfiguration : IEntityTypeConfiguration<Observer>
{
    public void Configure(EntityTypeBuilder<Observer> builder)
    {
        builder.ToTable("observers");
        builder.HasKey(observer => observer.Id)
            .HasName("pk_observers");

        builder.HasOne<EntityIndexRow>()
            .WithOne()
            .HasForeignKey<Observer>(observer => observer.Id)
            .HasConstraintName("fk_observers_entities_id")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(observer => observer.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(observer => observer.Name)
            .HasColumnName("name")
            .HasColumnType("text")
            .IsRequired();
    }
}
