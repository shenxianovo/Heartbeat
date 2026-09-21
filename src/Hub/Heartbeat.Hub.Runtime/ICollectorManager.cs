using Heartbeat.Management;

namespace Heartbeat.Hub.Runtime;

public interface ICollectorManager
{
    IReadOnlyList<CollectorType> Types { get; }
    IReadOnlyList<CollectorState> Collectors { get; }
    Task ExecuteAsync(CollectorOperation operation, CancellationToken cancellationToken);
}
