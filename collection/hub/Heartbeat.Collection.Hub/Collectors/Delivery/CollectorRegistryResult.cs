namespace Heartbeat.Collection.Hub.Collectors.Delivery;

/// <summary>
/// The outcome of a Registry read. Failures are structured values, not exceptions, so callers never
/// match on message text.
/// </summary>
public sealed class CollectorRegistryResult<T>
    where T : class
{
    private CollectorRegistryResult(T? value, CollectorRegistryFailureReason? reason, string? detail)
    {
        Value = value;
        Reason = reason;
        Detail = detail;
    }

    public T? Value { get; }

    /// <summary>The stable failure reason, or <c>null</c> when the read succeeded.</summary>
    public CollectorRegistryFailureReason? Reason { get; }

    /// <summary>Human-readable diagnostic context. Never branch on this string.</summary>
    public string? Detail { get; }

    public bool IsSuccess => Reason is null;

    public static CollectorRegistryResult<T> Success(T value) => new(value, null, null);

    public static CollectorRegistryResult<T> Failure(CollectorRegistryFailureReason reason, string detail) =>
        new(null, reason, detail);

    internal static CollectorRegistryResult<T> Failure<TOther>(CollectorRegistryResult<TOther> other)
        where TOther : class =>
        new(null, other.Reason, other.Detail);

    public T Require() => Value ?? throw new InvalidOperationException(
        $"Registry read failed with {Reason}: {Detail}");
}
