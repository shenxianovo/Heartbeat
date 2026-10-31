using System.Text.RegularExpressions;
using Orisumi.Infrastructure.Database.Converters;
using Microsoft.EntityFrameworkCore;

namespace Orisumi.Infrastructure.Database;

public sealed partial class OrisumiDbContext(DbContextOptions<OrisumiDbContext> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetValueConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("btree_gist");
        ConfigureManagement(modelBuilder);
        var contents = modelBuilder.Entity<StoredContent>();
        contents.ToTable("contents", table => table.HasCheckConstraint("ck_contents_size", "size = octet_length(data)"));
        contents.HasKey(row => row.Digest).HasName("pk_contents");
        contents.HasIndex(row => row.UploadedAt).HasDatabaseName("ix_contents_uploaded_at");
        var usages = modelBuilder.Entity<StoredVersionContent>();
        usages.ToTable("version_contents");
        usages.HasKey(row => new { row.VersionId, row.Digest }).HasName("pk_version_contents");
        usages.HasIndex(row => row.Digest).HasDatabaseName("ix_version_contents_digest");
        usages.HasOne<StoredVersion>().WithMany().HasForeignKey(row => row.VersionId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_version_contents_versions_version_id");
        usages.HasOne<StoredContent>().WithMany().HasForeignKey(row => row.Digest).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("fk_version_contents_contents_digest");
        var entities = modelBuilder.Entity<StoredEntity>();
        entities.ToTable("entities");
        entities.HasKey(row => row.EntityId).HasName("pk_entities");
        entities.HasIndex(row => row.CurrentVersionId).IsUnique().HasFilter("current_version_id IS NOT NULL").HasDatabaseName("ux_entities_current_version_id");
        entities.HasOne<StoredVersion>().WithMany().HasForeignKey(row => row.CurrentVersionId)
            .OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_entities_versions_current_version_id");
        entities.HasIndex(row => new { row.SchemaId, row.EntityId }).HasDatabaseName("ix_entities_schema_id_entity_id");
        entities.HasIndex(row => new { row.Category, row.EntityId }).HasDatabaseName("ix_entities_category_entity_id");
        var schemas = modelBuilder.Entity<StoredSchema>();
        schemas.ToTable("schemas");
        schemas.HasKey(row => row.EntityId).HasName("pk_schemas");
        schemas.HasIndex(row => row.ResourceName).IsUnique().HasDatabaseName("ux_schemas_resource_name");
        schemas.HasOne<StoredEntity>().WithMany().HasForeignKey(row => row.EntityId)
            .OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_schemas_entities_entity_id");
        var versions = modelBuilder.Entity<StoredVersion>();
        versions.ToTable("versions", table => table.HasCheckConstraint("ck_versions_observation",
            "(category = 'observation' AND schema_version_id IS NOT NULL AND observer_id IS NOT NULL AND formation IS NOT NULL) OR (category <> 'observation' AND schema_version_id IS NULL AND observer_id IS NULL AND formation IS NULL)"));
        versions.HasKey(row => row.VersionId).HasName("pk_versions");
        versions.Property(row => row.Content).HasColumnType("jsonb");
        versions.HasIndex(row => new { row.EntityId, row.Ordinal }).IsUnique().HasDatabaseName("ux_versions_entity_id_ordinal");
        versions.HasIndex(row => row.PreviousVersionId).IsUnique().HasFilter("previous_version_id IS NOT NULL").HasDatabaseName("ux_versions_previous_version_id");
        versions.HasIndex(row => row.EntityId).IsUnique().HasFilter("previous_version_id IS NULL").HasDatabaseName("ux_versions_entity_id");
        versions.HasOne<StoredVersion>().WithMany().HasForeignKey(row => row.PreviousVersionId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_versions_versions_previous_version_id");
        versions.HasOne<StoredVersion>().WithMany().HasForeignKey(row => row.SchemaVersionId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_versions_versions_schema_version_id");
        var references = modelBuilder.Entity<StoredVersionReference>();
        references.ToTable("version_references");
        references.HasKey(row => new { row.TargetEntityId, row.VersionId }).HasName("pk_version_references");
        references.HasIndex(row => row.VersionId).HasDatabaseName("ix_version_references_version_id");
        references.HasOne<StoredVersion>().WithMany().HasForeignKey(row => row.VersionId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_version_references_versions_version_id");
        var tombstones = modelBuilder.Entity<DeletedVersion>();
        tombstones.ToTable("deleted_versions");
        tombstones.HasKey(row => row.VersionId).HasName("pk_deleted_versions");
        tombstones.HasIndex(row => row.EntityId).HasDatabaseName("ix_deleted_versions_entity_id");
        var observations = modelBuilder.Entity<StoredObservation>();
        observations.ToTable("observations");
        observations.HasKey(row => row.EntityId).HasName("pk_observations");
        observations.Property(row => row.FactRange).HasColumnType("tstzrange");
        observations.HasOne<StoredEntity>().WithMany().HasForeignKey(row => row.EntityId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_observations_entities_entity_id");
        observations.HasOne<StoredVersion>().WithMany().HasForeignKey(row => row.VersionId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_observations_versions_version_id");
        observations.HasIndex(row => row.VersionId).IsUnique().HasDatabaseName("ux_observations_version_id");
        observations.HasIndex(row => new { row.SchemaId, row.FactRange }).HasMethod("gist").HasDatabaseName("ix_observations_schema_id_fact_range");
        observations.HasIndex(row => row.FactRange).HasMethod("gist").HasDatabaseName("ix_observations_fact_range");
        observations.HasIndex(row => row.ObserverEntityId).HasDatabaseName("ix_observations_observer_entity_id");
        observations.HasIndex(row => row.Formation).HasDatabaseName("ix_observations_formation");
        var things = modelBuilder.Entity<StoredThing>();
        things.ToTable("things");
        things.HasKey(row => row.EntityId).HasName("pk_things");
        var reviews = modelBuilder.Entity<StoredReview>();
        reviews.ToTable("reviews");
        reviews.HasKey(row => row.EntityId).HasName("pk_reviews");
        reviews.HasIndex(row => row.TargetEntityId).HasDatabaseName("ix_reviews_target_entity_id");
        var recognition = modelBuilder.Entity<StoredRecognitionKey>();
        recognition.ToTable("recognition_keys");
        recognition.HasNoKey();
        recognition.HasIndex(row => new { row.SchemaId, row.Scope, row.Scheme, row.Value }).IsUnique().AreNullsDistinct(false)
            .HasDatabaseName("ux_recognition_keys_schema_id_scope_scheme_value");
        recognition.HasIndex(row => row.EntityId).HasDatabaseName("ix_recognition_keys_entity_id");
        var streams = modelBuilder.Entity<StoredWriteStreamState>();
        streams.ToTable("write_streams");
        streams.HasKey(row => row.ObserverEntityId).HasName("pk_write_streams");
        streams.Property(row => row.SchemaVersionIds).HasColumnType("jsonb");
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(WordBoundary().Replace(property.Name, "$1_$2").ToLowerInvariant());
            foreach (var index in entity.GetIndexes())
                if (index.GetDatabaseName()!.StartsWith("IX_", StringComparison.Ordinal))
                    index.SetDatabaseName("ix_" + entity.GetTableName() + "_" + string.Join('_', index.Properties.Select(property => property.GetColumnName())));
        }
    }

    private static void ConfigureManagement(ModelBuilder modelBuilder)
    {
        var hosts = modelBuilder.Entity<StoredHost>();
        hosts.ToTable("observer_hosts", table => table.HasCheckConstraint("ck_observer_hosts_kind", "kind IN ('device', 'server')"));
        hosts.HasKey(row => row.HostId).HasName("pk_observer_hosts");
        var instances = modelBuilder.Entity<StoredInstance>();
        instances.ToTable("observer_instances", table => table.HasCheckConstraint("ck_observer_instances_settings", "jsonb_typeof(settings) = 'object'"));
        instances.HasKey(row => row.EntityId).HasName("pk_observer_instances");
        instances.Property(row => row.Settings).HasColumnType("jsonb");
        instances.Property(row => row.Status).HasColumnType("jsonb");
        instances.HasOne<StoredHost>().WithMany().HasForeignKey(row => row.HostId)
            .OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_observer_instances_observer_hosts_host_id");
        instances.HasIndex(row => new { row.HostId, row.PackageId }).IsUnique().HasFilter("removed = false")
            .HasDatabaseName("ux_observer_instances_host_id_package_id");
        instances.HasIndex(row => row.PackageId).HasFilter("removed = false").HasDatabaseName("ix_observer_instances_package_id");
    }

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex WordBoundary();
}
