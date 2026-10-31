using NpgsqlTypes;

namespace Orisumi.Infrastructure.Database;

internal sealed class StoredEntity
{
    public Guid EntityId { get; set; }
    public string Category { get; set; } = null!;
    public Guid? SchemaId { get; set; }
    public Guid? CurrentVersionId { get; set; }
}

internal sealed class StoredVersion
{
    public Guid VersionId { get; set; }
    public Guid EntityId { get; set; }
    public long Ordinal { get; set; }
    public Guid? PreviousVersionId { get; set; }
    public Guid? SchemaVersionId { get; set; }
    public string Category { get; set; } = null!;
    public DateTimeOffset RecordedAt { get; set; }
    public string Content { get; set; } = null!;
    public Guid? ObserverId { get; set; }
    public Guid? ObserverVersionId { get; set; }
    public string? Formation { get; set; }
    public Guid? InputSetVersionId { get; set; }
    public DateTimeOffset? StartAt { get; set; }
    public DateTimeOffset? EndAt { get; set; }
    public string? TimeZone { get; set; }
}

internal sealed class DeletedVersion
{
    public Guid VersionId { get; set; }
    public Guid EntityId { get; set; }
}

internal sealed class StoredVersionReference
{
    public Guid VersionId { get; set; }
    public Guid TargetEntityId { get; set; }
}

internal sealed class StoredObservation
{
    public Guid EntityId { get; set; }
    public Guid VersionId { get; set; }
    public Guid ObserverEntityId { get; set; }
    public Guid SchemaId { get; set; }
    public string Formation { get; set; } = null!;
    public NpgsqlRange<DateTimeOffset>? FactRange { get; set; }
}

internal sealed class StoredSchema
{
    public Guid EntityId { get; set; }
    public string ResourceName { get; set; } = null!;
}

internal sealed class StoredHost
{
    public Guid HostId { get; set; }
    public string Name { get; set; } = null!;
    public string Kind { get; set; } = null!;
    public DateTimeOffset? LastSeenAt { get; set; }
    public string CredentialHash { get; set; } = null!;
    public bool Revoked { get; set; }
}

internal sealed class StoredInstance
{
    public Guid EntityId { get; set; }
    public Guid HostId { get; set; }
    public string PackageId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public bool Enabled { get; set; }
    public string Settings { get; set; } = null!;
    public long Revision { get; set; }
    public long LastLocalSequence { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset? ReportedAt { get; set; }
    public bool Removed { get; set; }
}

internal sealed class StoredContent
{
    public string Digest { get; set; } = null!;
    public long Size { get; set; }
    public byte[] Data { get; set; } = null!;
    public DateTimeOffset UploadedAt { get; set; }
}

internal sealed class StoredVersionContent
{
    public Guid VersionId { get; set; }
    public string Digest { get; set; } = null!;
}

internal sealed class StoredRecognitionKey
{
    public Guid SchemaId { get; set; }
    public string? Scope { get; set; }
    public string Scheme { get; set; } = null!;
    public string Value { get; set; } = null!;
    public Guid EntityId { get; set; }
}
internal sealed class StoredWriteStreamState
{
    public Guid ObserverEntityId { get; set; }
    public Guid? DescriptionVersionId { get; set; }
    public long LastSequence { get; set; }
    public string SchemaVersionIds { get; set; } = null!;
}

internal sealed class StoredThing
{
    public Guid EntityId { get; set; }
    public Guid SchemaId { get; set; }
    public bool Deleted { get; set; }
}
internal sealed class StoredReview
{
    public Guid EntityId { get; set; }
    public Guid TargetEntityId { get; set; }
    public string Verdict { get; set; } = null!;
    public bool ByPerson { get; set; }
    public bool Whole { get; set; }
}
