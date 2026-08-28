namespace Heartbeat.Collection.CollectorProtocol;

/// <summary>
/// A Transport Binding at the Collector Protocol seam. Implementations translate transport
/// mechanics only; lifecycle and durable delivery remain owned by <see cref="CollectorProtocolClient"/>.
/// </summary>
public interface ICollectorProtocolBinding : IAsyncDisposable
{
    ValueTask<CollectorClientInitialization> StartAsync(
        CollectorClientDefinition definition,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyDictionary<string, CollectorClientStream>> OpenStreamsAsync(
        long specRevision,
        IReadOnlyList<CollectorOutputBinding> outputs,
        CancellationToken cancellationToken);

    ValueTask ReadyAsync(long appliedSpecRevision, CancellationToken cancellationToken);

    ValueTask<CollectorFactBatchAcknowledgement> PublishAsync(
        Guid messageId,
        IReadOnlyList<BoundCollectorFact> facts,
        CancellationToken cancellationToken);

    ValueTask<CollectorGapDeliveryOutcome> ReportGapAsync(
        Guid messageId,
        Guid streamId,
        CollectorStreamGap gap,
        CancellationToken cancellationToken);

    ValueTask<CollectorAuthorizationResponse> ChallengeAsync(
        Guid interactionId,
        string kind,
        string title,
        string? message,
        IReadOnlyList<CollectorAuthorizationField> fields,
        CancellationToken cancellationToken);

    ValueTask CompleteAuthorizationAsync(Guid interactionId, CancellationToken cancellationToken);
    ValueTask<string?> ReadSecretAsync(string key, CancellationToken cancellationToken);
    ValueTask WriteSecretAsync(string key, string value, CancellationToken cancellationToken);
    ValueTask DeleteSecretAsync(string key, CancellationToken cancellationToken);
    ValueTask<CollectorDrainRequest> WaitForDrainAsync(CancellationToken cancellationToken);
    ValueTask CompleteDrainAsync(CollectorDrainResult result, CancellationToken cancellationToken);
}

public interface ICollectorProtocolApplication
{
    ValueTask InitializeAsync(CollectorActivation activation, CancellationToken cancellationToken);
    ValueTask StartAsync(CollectorActivation activation, CancellationToken cancellationToken);
    ValueTask StopAsync(CollectorActivation activation, CancellationToken cancellationToken);
}
