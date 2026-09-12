using Heartbeat.Recording;

namespace Heartbeat.Application.Recording;

public sealed record ResolveTrackCommand(
    Guid CollectorId,
    string? Type,
    int Version,
    TimeMode TimeMode,
    EndMode? EndMode);

public sealed record ResolvedTrack(
    Guid Id,
    Guid CollectorId,
    string Type,
    int Version,
    TimeMode TimeMode,
    EndMode? EndMode,
    DateTimeOffset CreatedAt);

public abstract record ResolveTrackResult
{
    private ResolveTrackResult()
    {
    }

    public sealed record Resolved(ResolvedTrack Track) : ResolveTrackResult;

    public sealed record CollectorNotFound : ResolveTrackResult;

    public sealed record DefinitionConflict : ResolveTrackResult;
}

public interface IResolveTrack
{
    Task<ResolveTrackResult> ExecuteAsync(
        Guid ownerId,
        ResolveTrackCommand command,
        CancellationToken cancellationToken = default);
}

public interface ITrackStore
{
    Task<Track?> FindAsync(Guid ownerId, Guid trackId, CancellationToken cancellationToken = default);

    Task<ResolvedTrack?> ResolveAsync(
        Guid ownerId,
        Track candidate,
        CancellationToken cancellationToken = default);
}

public sealed class ResolveTrack(ITrackStore store, TimeProvider timeProvider) : IResolveTrack
{
    public async Task<ResolveTrackResult> ExecuteAsync(
        Guid ownerId,
        ResolveTrackCommand command,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An owner is required.", nameof(ownerId));
        }

        ArgumentNullException.ThrowIfNull(command);
        if (command.CollectorId == Guid.Empty)
        {
            throw new ArgumentException("A collector is required.", nameof(command));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command.Type);
        ArgumentOutOfRangeException.ThrowIfLessThan(command.Version, 1);
        var candidate = Track.Create(command.CollectorId, command.Type, command.Version,
            command.TimeMode, command.EndMode, timeProvider.GetUtcNow());
        var track = await store.ResolveAsync(ownerId, candidate, cancellationToken);
        if (track is null)
        {
            return new ResolveTrackResult.CollectorNotFound();
        }

        if (track.TimeMode != candidate.TimeMode || track.EndMode != candidate.EndMode)
        {
            return new ResolveTrackResult.DefinitionConflict();
        }

        return new ResolveTrackResult.Resolved(track);
    }
}
