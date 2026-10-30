using Orisumi.Core;

namespace Orisumi.Application.Entities;

public sealed record SubmissionRejection(int Index, VersionId VersionId, string Rule, string Detail, bool Conflict = false);
public sealed record SubmissionResult(IReadOnlyList<EntityVersion> Saved, SubmissionRejection? Rejection)
{
    public IReadOnlyList<EntityVersion> RequireSuccess()
    {
        if (Rejection is { } rejected) throw new ModelViolationException(rejected.Rule, rejected.Detail, rejected.Conflict);
        return Saved;
    }
}

public sealed class EntityDeletedException() : Exception("The requested identity has been deleted.");
public sealed class ResourceNotFoundException() : Exception("No EntitySchema uses this resourceName.");

public sealed record EntityQuery(string? Category = null, EntityId? SchemaId = null, int Limit = 100, EntityId? After = null);
public sealed record ObservationCursor(DateTimeOffset Time, EntityId EntityId);
public sealed record ObservationQuery(DateTimeOffset From, DateTimeOffset To, EntityId? SchemaId = null,
    int Limit = 100, ObservationCursor? After = null);
