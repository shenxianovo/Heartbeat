namespace Orisumi.Core;

public sealed record EntityVersion(VersionSubmission Submission, DateTimeOffset RecordedAt)
{
    public EntityId EntityId => Submission.EntityId;
    public VersionId VersionId => Submission.VersionId;
    public VersionId? PreviousVersionId => Submission.PreviousVersionId;
    public VersionId? SchemaVersionId => Submission.SchemaVersionId;
    public string Category => Submission.Category;
    public VersionContent Content => Submission.Content;
}
