using System.Text.Json;

namespace Orisumi.Application.Management;

public interface IManagementStore
{
    Task<IReadOnlyList<ObserverHost>> HostsAsync(CancellationToken token);
    Task<ObserverHost> HostAsync(Guid id, CancellationToken token);
    Task<ObserverHost> AuthenticateAsync(string hash, CancellationToken token);
    Task<Guid> PairAsync(Guid? id, string name, string kind, string hash, CancellationToken token);
    Task<ObserverHost> RenameAsync(Guid id, string name, CancellationToken token);
    Task RevokeAsync(Guid id, CancellationToken token);
    Task<IReadOnlyList<ObserverInstance>> InstancesAsync(Guid? host, CancellationToken token);
    Task<ObserverInstance> InstanceAsync(Guid id, CancellationToken token);
    Task<IReadOnlyList<InstanceConfig>> ConfigurationsAsync(Guid host, CancellationToken token);
    Task<InstanceConfig> RegisterAsync(Guid host, Guid id, InstanceRegistration registration, CancellationToken token);
    Task<InstanceConfig> ChangeAsync(Guid? host, Guid id, ConfigurationChange change, long? sequence, CancellationToken token);
    Task ReportAsync(Guid host, Guid id, JsonElement status, CancellationToken token);
}
