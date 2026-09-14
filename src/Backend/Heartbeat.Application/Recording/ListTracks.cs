using Heartbeat.Recording;

namespace Heartbeat.Application.Recording;

public sealed record ListedTrack(
    Guid Id,
    Guid CollectorId,
    string CollectorKey,
    string CollectorTarget,
    string CollectorDisplayName,
    string Type,
    int Version,
    TimeMode TimeMode,
    EndMode? EndMode,
    DateTimeOffset CreatedAt);

public interface IListTracks
{
    Task<IReadOnlyList<ListedTrack>> ExecuteAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default);
}

public sealed class ListTracks(ITrackStore store) : IListTracks
{
    public Task<IReadOnlyList<ListedTrack>> ExecuteAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An owner is required.", nameof(ownerId));
        }

        return store.ListAsync(ownerId, cancellationToken);
    }
}
