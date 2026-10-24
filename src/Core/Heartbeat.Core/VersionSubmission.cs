namespace Heartbeat.Core;

// VERSION-10: a submission cannot carry a recordedAt supplied by the writer.
public sealed record VersionSubmission(
    EntityId EntityId,
    VersionId VersionId,
    VersionId? PreviousVersionId,
    VersionId? SchemaVersionId,
    VersionContent Content)
{
    public string Category => Content.Category;
}
