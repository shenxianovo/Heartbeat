namespace Orisumi.Core;

// OBSERVATION-11: a live statement, without a VersionId or recordedAt.
public sealed record ObservationDraft(EntityId EntityId, VersionId? PreviousVersionId, long Sequence,
    Observation Content, DateTimeOffset UpdatedAt);
