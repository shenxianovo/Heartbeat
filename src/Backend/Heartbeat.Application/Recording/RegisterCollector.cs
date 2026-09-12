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

public abstract record RegisterCollectorResult
{
    private RegisterCollectorResult()
    {
    }

    public sealed record Registered(RegisteredCollector Collector) : RegisterCollectorResult;

    public sealed record TimelineNotProvisioned : RegisterCollectorResult;
}

public interface IRegisterCollector
{
    Task<RegisterCollectorResult> ExecuteAsync(
        Guid ownerId,
        RegisterCollectorCommand command,
        CancellationToken cancellationToken = default);
}

public interface ICollectorRegistrationStore
{
    Task<RegisteredCollector?> RegisterAsync(
        Guid ownerId,
        Guid candidateCollectorId,
        CollectorRegistration registration,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);
}

public sealed class RegisterCollector(
    ICollectorRegistrationStore store,
    TimeProvider timeProvider) : IRegisterCollector
{
    public async Task<RegisterCollectorResult> ExecuteAsync(
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
        var candidateCollectorId = Guid.CreateVersion7(createdAt);
        var collector = await store.RegisterAsync(
            ownerId,
            candidateCollectorId,
            registration,
            createdAt,
            cancellationToken);

        return collector is null
            ? new RegisterCollectorResult.TimelineNotProvisioned()
            : new RegisterCollectorResult.Registered(collector);
    }
}
