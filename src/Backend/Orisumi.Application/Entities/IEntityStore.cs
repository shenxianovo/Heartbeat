namespace Orisumi.Application.Entities;

public interface IEntityStore
{
    // The store serializes this operation with all other reads/writes, across service instances.
    // It commits on success and rolls back on an exception or cancellation.
    Task<T> ExecuteAsync<T>(Func<IEntityTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);
}
