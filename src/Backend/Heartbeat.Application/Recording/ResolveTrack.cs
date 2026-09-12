using Heartbeat.Recording;
using Heartbeat.Recording.Protocols;

namespace Heartbeat.Application.Recording;

public sealed record ResolveTrackCommand(Guid CollectorId, string? Type, int Version);

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

    public sealed record UnsupportedProtocol : ResolveTrackResult;

    public sealed record ProtocolConflict : ResolveTrackResult;
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
        var protocol = RecordProtocols.Find(command.Type.Trim(), command.Version);
        if (protocol is null)
        {
            return new ResolveTrackResult.UnsupportedProtocol();
        }

        var candidate = Track.Create(command.CollectorId, protocol.Type, protocol.Version,
            protocol.TimeMode, protocol.EndMode, timeProvider.GetUtcNow());
        var track = await store.ResolveAsync(ownerId, candidate, cancellationToken);
        if (track is null)
        {
            return new ResolveTrackResult.CollectorNotFound();
        }

        if (track.TimeMode != protocol.TimeMode || track.EndMode != protocol.EndMode)
        {
            return new ResolveTrackResult.ProtocolConflict();
        }

        return new ResolveTrackResult.Resolved(track);
    }
}
