using Orisumi.Core;

namespace Orisumi.Application.Observers;

public interface IServerObserverStore
{
    Task<EntityId> GetOrCreateAsync(string key, CancellationToken token);
    Task<string?> ReadPendingAsync(EntityId instance, CancellationToken token);
    Task SavePendingAsync(EntityId instance, string payload, CancellationToken token);
    Task ClearPendingAsync(EntityId instance, CancellationToken token);
    Task<string?> ReadStateAsync(EntityId instance, CancellationToken token);
    Task SaveStateAsync(EntityId instance, string payload, CancellationToken token);
}
