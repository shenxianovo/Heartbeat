using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Heartbeat.Persistence.Configurations;

internal sealed class ObjectConfiguration : IEntityTypeConfiguration<RecordingObject>
{
    public void Configure(EntityTypeBuilder<RecordingObject> builder)
    {
        builder.ToTable("objects");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(item => item.OwnerId).HasColumnName("owner_id");
        builder.HasIndex(item => item.OwnerId);
    }
}

internal sealed class ObjectBindingConfiguration : IEntityTypeConfiguration<ObjectBinding>
{
    public void Configure(EntityTypeBuilder<ObjectBinding> builder)
    {
        builder.ToTable("object_bindings");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        builder.Property(item => item.OwnerId).HasColumnName("owner_id");
        builder.Property(item => item.ScopeId).HasColumnName("scope_id");
        builder.Property(item => item.ObjectId).HasColumnName("object_id");
        builder.Property(item => item.Namespace).HasColumnName("identity_namespace").HasMaxLength(128).IsRequired();
        builder.Property(item => item.Key).HasColumnName("identity_key").HasMaxLength(512).IsRequired();
        builder.HasIndex(item => new { item.OwnerId, item.ScopeId, item.Namespace, item.Key }).IsUnique().AreNullsDistinct(false);
        builder.HasOne<RecordingObject>().WithMany().HasForeignKey(item => item.ObjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RecordingObject>().WithMany().HasForeignKey(item => item.ScopeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ObjectDescriptionConfiguration : IEntityTypeConfiguration<ObjectDescription>
{
    public void Configure(EntityTypeBuilder<ObjectDescription> builder)
    {
        builder.ToTable("object_descriptions");
        builder.HasKey(item => item.ObjectId);
        builder.Property(item => item.ObjectId).HasColumnName("object_id").ValueGeneratedNever();
        builder.Property(item => item.Name).HasColumnName("name").HasMaxLength(512);
        builder.Property(item => item.ObservedAt).HasColumnName("observed_at");
        builder.Property(item => item.RecordId).HasColumnName("record_id");
        builder.HasOne<RecordingObject>().WithOne().HasForeignKey<ObjectDescription>(item => item.ObjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Record>().WithMany().HasForeignKey(item => item.RecordId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RecordObjectConfiguration : IEntityTypeConfiguration<RecordObject>
{
    public void Configure(EntityTypeBuilder<RecordObject> builder)
    {
        builder.ToTable("record_objects");
        builder.HasKey(item => new { item.RecordId, item.ReferenceIndex });
        builder.Property(item => item.RecordId).HasColumnName("record_id");
        builder.Property(item => item.ReferenceIndex).HasColumnName("reference_index");
        builder.Property(item => item.ObjectId).HasColumnName("object_id");
        builder.Property(item => item.Role).HasColumnName("role").HasMaxLength(64);
        builder.HasIndex(item => new { item.ObjectId, item.RecordId });
        builder.HasOne<Record>().WithMany().HasForeignKey(item => item.RecordId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RecordingObject>().WithMany().HasForeignKey(item => item.ObjectId).OnDelete(DeleteBehavior.Restrict);
    }
}
