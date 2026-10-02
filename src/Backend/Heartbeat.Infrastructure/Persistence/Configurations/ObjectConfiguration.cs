using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Heartbeat.Persistence.Configurations;

internal sealed class ObjectConfiguration : IEntityTypeConfiguration<ObservedObject>
{
    public void Configure(EntityTypeBuilder<ObservedObject> builder)
    {
        builder.ToTable("objects");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(item => item.TimelineId).HasColumnName("timeline_id");
        builder.Property(item => item.Namespace).HasColumnName("identity_namespace").HasMaxLength(128).IsRequired();
        builder.Property(item => item.Key).HasColumnName("identity_key").HasMaxLength(512).IsRequired();
        builder.Property(item => item.Name).HasColumnName("name").HasMaxLength(512);
        builder.Property(item => item.NameObservedAt).HasColumnName("name_observed_at");
        builder.Property(item => item.NameRecordId).HasColumnName("name_record_id");
        builder.HasIndex(item => new { item.TimelineId, item.Namespace, item.Key }).IsUnique();
        builder.HasOne<Timeline>().WithMany().HasForeignKey(item => item.TimelineId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RecordObjectConfiguration : IEntityTypeConfiguration<RecordObject>
{
    public void Configure(EntityTypeBuilder<RecordObject> builder)
    {
        builder.ToTable("record_objects");
        builder.HasKey(item => new { item.RecordId, item.ObjectId, item.Role });
        builder.Property(item => item.RecordId).HasColumnName("record_id");
        builder.Property(item => item.ObjectId).HasColumnName("object_id");
        builder.Property(item => item.Role).HasColumnName("role").HasMaxLength(64);
        builder.HasIndex(item => new { item.ObjectId, item.RecordId });
        builder.HasOne<Record>().WithMany().HasForeignKey(item => item.RecordId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(item => item.Subject).WithMany().HasForeignKey(item => item.ObjectId).OnDelete(DeleteBehavior.Restrict);
    }
}
