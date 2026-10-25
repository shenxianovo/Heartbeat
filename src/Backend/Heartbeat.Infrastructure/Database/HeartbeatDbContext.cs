using System.Text.RegularExpressions;
using Heartbeat.Infrastructure.Database.Converters;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Infrastructure.Database;

public sealed partial class HeartbeatDbContext(DbContextOptions<HeartbeatDbContext> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetValueConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("btree_gist");
        var entities = modelBuilder.Entity<StoredEntity>();
        entities.ToTable("entities", table => table.HasCheckConstraint("ck_entities_deleted_state", """
            (deleted_at IS NULL AND category IS NOT NULL AND current_version_id IS NOT NULL)
            OR (deleted_at IS NOT NULL AND category IS NULL AND schema_id IS NULL
                AND resource_name IS NULL AND current_version_id IS NULL)
            """));
        entities.HasKey(row => row.EntityId).HasName("pk_entities");
        entities.HasIndex(row => row.ResourceName).IsUnique().HasFilter("resource_name IS NOT NULL").HasDatabaseName("ux_entities_resource_name");
        entities.HasIndex(row => new { row.SchemaId, row.EntityId }).HasDatabaseName("ix_entities_schema_id_entity_id");
        entities.HasIndex(row => new { row.Category, row.EntityId }).HasDatabaseName("ix_entities_category_entity_id");
        var versions = modelBuilder.Entity<StoredVersion>();
        versions.ToTable("versions");
        versions.HasKey(row => row.VersionId).HasName("pk_versions");
        versions.Property(row => row.Content).HasColumnType("jsonb");
        versions.HasIndex(row => new { row.EntityId, row.Ordinal }).IsUnique().HasDatabaseName("ux_versions_entity_id_ordinal");
        versions.HasOne<StoredEntity>().WithMany().HasForeignKey(row => row.EntityId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_versions_entities_entity_id");
        versions.HasOne<StoredVersion>().WithMany().HasForeignKey(row => row.PreviousVersionId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_versions_versions_previous_version_id");
        versions.HasOne<StoredVersion>().WithMany().HasForeignKey(row => row.SchemaVersionId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_versions_versions_schema_version_id");
        var references = modelBuilder.Entity<StoredVersionReference>();
        references.ToTable("version_references");
        references.HasKey(row => new { row.VersionId, row.Path }).HasName("pk_version_references");
        references.HasIndex(row => new { row.TargetEntityId, row.VersionId }).HasDatabaseName("ix_version_references_target_entity_id_version_id");
        references.HasOne<StoredVersion>().WithMany().HasForeignKey(row => row.VersionId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_version_references_versions_version_id");
        references.HasOne<StoredEntity>().WithMany().HasForeignKey(row => row.TargetEntityId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_version_references_entities_target_entity_id");
        var tombstones = modelBuilder.Entity<VersionTombstone>();
        tombstones.ToTable("version_tombstones");
        tombstones.HasKey(row => row.VersionId).HasName("pk_version_tombstones");
        tombstones.HasOne<StoredEntity>().WithMany().HasForeignKey(row => row.EntityId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_version_tombstones_entities_entity_id");
        var observations = modelBuilder.Entity<CurrentObservation>();
        observations.ToTable("current_observations");
        observations.HasKey(row => row.EntityId).HasName("pk_current_observations");
        observations.Property(row => row.FactRange).HasColumnType("tstzrange");
        observations.HasOne<StoredEntity>().WithMany().HasForeignKey(row => row.EntityId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_current_observations_entities_entity_id");
        observations.HasOne<StoredVersion>().WithMany().HasForeignKey(row => row.VersionId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_current_observations_versions_version_id");
        observations.HasIndex(row => new { row.ContentSchemaId, row.FactRange }).HasMethod("gist").HasDatabaseName("ix_current_observations_content_schema_id_fact_range");
        observations.HasIndex(row => row.FactRange).HasMethod("gist").HasDatabaseName("ix_current_observations_fact_range");
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(WordBoundary().Replace(property.Name, "$1_$2").ToLowerInvariant());
            foreach (var index in entity.GetIndexes())
                if (index.GetDatabaseName()!.StartsWith("IX_", StringComparison.Ordinal))
                    index.SetDatabaseName("ix_" + entity.GetTableName() + "_" + string.Join('_', index.Properties.Select(property => property.GetColumnName())));
        }
    }

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex WordBoundary();
}
