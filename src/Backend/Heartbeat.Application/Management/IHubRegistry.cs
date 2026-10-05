namespace Heartbeat.Management;

public interface IHubRegistry
{
    Task<bool> ReportAsync(Guid owner, Guid id, Guid sessionId, HubReport report, CancellationToken cancellationToken);
    Task<IReadOnlyList<HubSummary>> ListAsync(Guid owner, CancellationToken cancellationToken);
    Task<bool> RetireAsync(Guid owner, Guid id, CancellationToken cancellationToken);
}
