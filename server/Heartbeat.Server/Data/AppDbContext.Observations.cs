using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Data;

public partial class AppDbContext
{
    public DbSet<ObservationCollector> Collectors => Set<ObservationCollector>();
    public DbSet<ObservationObject> Objects => Set<ObservationObject>();
    public DbSet<ObjectRelation> Relations => Set<ObjectRelation>();
    public DbSet<RelationMember> RelationMembers => Set<RelationMember>();

    private static void ConfigureObservations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ObservationCollector>(entity =>
        {
            entity.ToTable("Collectors");
            entity.HasKey(e => new { e.OwnerId, e.Id });
            entity.Property(e => e.Id).ValueGeneratedNever();
        });
        modelBuilder.Entity<ObservationObject>(entity =>
        {
            entity.ToTable("Objects", table => table.HasCheckConstraint("CK_Objects_KindOwner",
                "(\"Kind\" = 'app' AND \"OwnerId\" IS NULL) OR (\"Kind\" IN ('machine','account','person') AND \"OwnerId\" IS NOT NULL)"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.HasIndex(e => new { e.OwnerId, e.Kind, e.Scope, e.Key }).IsUnique().HasFilter("\"OwnerId\" IS NOT NULL");
            entity.HasIndex(e => new { e.Kind, e.Scope, e.Key }).IsUnique().HasFilter("\"OwnerId\" IS NULL");
        });
        modelBuilder.Entity<FactRecord>(entity =>
        {
            entity.HasAlternateKey(e => new { e.OwnerId, e.Id });
            entity.HasOne<ObservationCollector>().WithMany().HasForeignKey(e => new { e.OwnerId, e.ObserverId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ObservationObject>().WithMany().HasForeignKey(e => e.FoiId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.FoiId).ValueGeneratedOnAddOrUpdate();
            entity.Property(e => e.Aspect).ValueGeneratedOnAddOrUpdate();
            entity.HasIndex(e => new { e.OwnerId, e.FoiId });
        });
        modelBuilder.Entity<ObjectRelation>(entity =>
        {
            entity.ToTable("Relations", table => table.HasCheckConstraint("CK_Relations_Time",
                "(\"ValidFrom\" IS NULL OR isfinite(\"ValidFrom\")) AND (\"ValidTo\" IS NULL OR isfinite(\"ValidTo\")) AND (\"ValidFrom\" IS NULL OR \"ValidTo\" IS NULL OR \"ValidFrom\" <= \"ValidTo\")"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Evidence).HasColumnType("jsonb");
            entity.Property(e => e.FactId).HasComputedColumnSql("(\"Evidence\"->>'factId')::uuid", stored: true);
            entity.Property(e => e.AssociationId).HasComputedColumnSql("(\"Evidence\"->>'associationId')::bigint", stored: true);
            entity.HasOne<FactRecord>().WithMany().HasForeignKey(e => new { e.OwnerId, e.FactId })
                .HasPrincipalKey(e => new { e.OwnerId, e.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PersonAssociation>().WithMany().HasForeignKey(e => new { e.OwnerId, e.AssociationId })
                .HasPrincipalKey(e => new { e.OwnerId, e.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.OwnerId, e.Kind, e.FactId }).IsUnique().HasFilter("\"FactId\" IS NOT NULL");
            entity.HasIndex(e => new { e.OwnerId, e.Kind, e.AssociationId }).IsUnique().HasFilter("\"AssociationId\" IS NOT NULL");
        });
        modelBuilder.Entity<RelationMember>(entity =>
        {
            entity.ToTable("RelationMembers");
            entity.HasKey(e => new { e.RelationId, e.Role, e.ObjectId });
            entity.HasOne(e => e.Relation).WithMany(e => e.Members).HasForeignKey(e => e.RelationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Object).WithMany().HasForeignKey(e => e.ObjectId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.ObjectId, e.Role });
        });
        ObjectReference<Device>(modelBuilder);
        ObjectReference<App>(modelBuilder);
        ObjectReference<ServiceAccount>(modelBuilder);
        ObjectReference<Person>(modelBuilder);
    }

    private static void ObjectReference<T>(ModelBuilder modelBuilder) where T : class
    {
        var entity = modelBuilder.Entity<T>();
        entity.Property<Guid?>("ObjectId").ValueGeneratedOnAddOrUpdate();
        entity.HasOne<ObservationObject>().WithMany().HasForeignKey("ObjectId").OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex("ObjectId").IsUnique();
    }
}
