using NpgsqlTypes;

namespace Heartbeat.Infrastructure.Database;

internal sealed class StoredEntity
{
    public Guid EntityId { get; set; }
    public string? Category { get; set; }
    public Guid? SchemaId { get; set; }
    public string? ResourceName { get; set; }
    public Guid? CurrentVersionId { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

internal sealed class StoredVersion
{
    public Guid VersionId { get; set; }
    public Guid EntityId { get; set; }
    public int Ordinal { get; set; }
    public Guid? PreviousVersionId { get; set; }
    public Guid? SchemaVersionId { get; set; }
    public string Category { get; set; } = null!;
    public DateTimeOffset RecordedAt { get; set; }
    public string Content { get; set; } = null!;
}

internal sealed class VersionTombstone
{
    public Guid VersionId { get; set; }
    public Guid EntityId { get; set; }
}

internal sealed class StoredVersionReference
{
    public Guid VersionId { get; set; }
    public string Path { get; set; } = null!;
    public Guid TargetVersionId { get; set; }
    public Guid TargetEntityId { get; set; }
}

internal sealed class CurrentObservation
{
    public Guid EntityId { get; set; }
    public Guid VersionId { get; set; }
    public Guid ContentSchemaId { get; set; }
    public NpgsqlRange<DateTimeOffset>? FactRange { get; set; }
}
