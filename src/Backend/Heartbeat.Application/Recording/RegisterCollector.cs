using Heartbeat.Recording;

namespace Heartbeat.Application.Recording;

public sealed record RegisterCollectorCommand(
    string? Key,
    string? Target,
    string? DisplayName);

public sealed record RegisteredCollector(
    Guid Id,
    string Key,
    string Target,
    string DisplayName,
    DateTimeOffset CreatedAt);

public interface IRegisterCollector
{
    Task<RegisteredCollector> ExecuteAsync(
        Guid ownerId,
        RegisterCollectorCommand command,
        CancellationToken cancellationToken = default);
}

public interface ICollectorRegistrationStore
{
    Task<RegisteredCollector> RegisterAsync(
        Timeline candidateTimeline,
        RecordingObject candidateObject,
        CollectorRegistration registration,
        CancellationToken cancellationToken = default);
}

public sealed class RegisterCollector(
    ICollectorRegistrationStore store,
    TimeProvider timeProvider) : IRegisterCollector
{
    public async Task<RegisteredCollector> ExecuteAsync(
        Guid ownerId,
        RegisterCollectorCommand command,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An owner is required.", nameof(ownerId));
        }

        ArgumentNullException.ThrowIfNull(command);

        var registration = CollectorRegistration.Create(
            command.Key,
            command.Target,
            command.DisplayName);
        var createdAt = timeProvider.GetUtcNow().ToUniversalTime();
        var candidateObject = RecordingObject.Create(ownerId);
        return await store.RegisterAsync(
            Timeline.Create(ownerId, "My Timeline", createdAt),
            candidateObject,
            registration,
            cancellationToken);
    }
}
