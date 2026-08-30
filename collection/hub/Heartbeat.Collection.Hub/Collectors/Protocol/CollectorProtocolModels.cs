using System.Collections.Immutable;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Hub.Collectors.Protocol;

public sealed record ProtocolSupport(
    IReadOnlyList<int> ProtocolMajors,
    IReadOnlyDictionary<string, IReadOnlyList<int>> Capabilities);

public sealed record CollectorInitialization(
    Guid ActivationId,
    CollectorInstance Instance,
    CollectorInstanceSpec Spec,
    VerifiedCollectorArtifact Artifact,
    CollectorProtocolLimits Limits,
    CollectorResources Resources);

public sealed record CollectorResources(string? DataDirectory);

public sealed record CollectorProtocolLimits(
    int MaxFactsPerBatch,
    int MaxBatchBytes);

public sealed record OutputBinding(
    string BindingId,
    string OutputId,
    IReadOnlyDictionary<string, string> Dimensions);

public sealed record InProcessCollectorInitialization(
    long AppliedSpecRevision,
    IReadOnlyList<OutputBinding> Bindings);

public enum CollectorDrainReason
{
    Drained,
    DeadlineExceeded,
    StopFailed,
    FlushCancelled,
    PersistenceFailed
}

public enum CollectorDrainCompletionReason
{
    Completed,
    DeadlineExceeded,
    CompletionFailed
}

public static class CollectorDrainVocabulary
{
    public static string Format(CollectorDrainReason reason) => reason switch
    {
        CollectorDrainReason.Drained => "drained",
        CollectorDrainReason.DeadlineExceeded => "deadline_exceeded",
        CollectorDrainReason.StopFailed => "stop_failed",
        CollectorDrainReason.FlushCancelled => "flush_cancelled",
        CollectorDrainReason.PersistenceFailed => "persistence_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    public static bool TryParse(string value, out CollectorDrainReason reason)
    {
        reason = value switch
        {
            "drained" => CollectorDrainReason.Drained,
            "deadline_exceeded" => CollectorDrainReason.DeadlineExceeded,
            "stop_failed" => CollectorDrainReason.StopFailed,
            "flush_cancelled" => CollectorDrainReason.FlushCancelled,
            "persistence_failed" => CollectorDrainReason.PersistenceFailed,
            _ => default
        };
        return value is "drained" or "deadline_exceeded" or "stop_failed" or
            "flush_cancelled" or "persistence_failed";
    }
}

public sealed record InProcessCollectorLogicalDrainResult(
    int? PendingFacts,
    int? PendingGaps,
    CollectorDrainReason Reason = CollectorDrainReason.Drained,
    bool RemainderDurable = true)
{
    public bool IsFullyDrained =>
        Reason == CollectorDrainReason.Drained &&
        RemainderDurable &&
        PendingFacts == 0 &&
        PendingGaps == 0;
}

public sealed record InProcessCollectorDrainResult(
    InProcessCollectorLogicalDrainResult LogicalResult,
    CollectorDrainCompletionReason CompletionReason = CollectorDrainCompletionReason.Completed,
    string? CompletionError = null)
{
    public int? PendingFacts => LogicalResult.PendingFacts;
    public int? PendingGaps => LogicalResult.PendingGaps;
    public bool IsFullyDrained =>
        CompletionReason == CollectorDrainCompletionReason.Completed && LogicalResult.IsFullyDrained;
}

public sealed record ExternalHostCollectorInitialization(
    Guid ActivationId,
    CollectorInstance Instance,
    CollectorInstanceSpec Spec,
    CollectorProtocolLimits Limits,
    CollectorResources Resources,
    IReadOnlyDictionary<string, int> SelectedCapabilities);

public interface IInProcessCollector
{
    string ArtifactId { get; }
    ProtocolSupport ProtocolSupport { get; }

    ValueTask<InProcessCollectorInitialization> InitializeAsync(
        CollectorInitialization initialization,
        CancellationToken cancellationToken);

    /// <summary>
    /// Receives the atomically opened Stream descriptors. The Collector must call
    /// <see cref="InProcessCollectorStreamsOpened.ReadyAsync"/> before it can publish, then return
    /// after any immediate startup work has been scheduled or completed.
    /// </summary>
    ValueTask OnStreamsOpenedAsync(
        InProcessCollectorStreamsOpened opened,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stops Collector-owned work. Completion is the stop-first boundary that permits a
    /// replacement Activation to start.
    /// </summary>
    ValueTask<InProcessCollectorDrainResult> StopAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional hard fence for an InProcess Collector whose private delivery loop can outlive a
/// cooperative stop request. The Hub invokes it synchronously before releasing writer ownership
/// after the drain deadline.
/// </summary>
public interface IInProcessCollectorDeadlineFence
{
    void FenceAfterDeadline();
}

public enum CollectorActivationState
{
    Negotiating,
    OpeningStreams,
    Ready,
    Draining,
    Stopped
}

public enum ExternalHostActivationStopReason
{
    LeaseExpired,
    LeaseReplaced,
    DesiredDisabled,
    CollectorDrained,
    RuntimeStopping
}

public enum CollectorHandshakeStep
{
    Hello,
    Initialize,
    StreamsOpen,
    Ready
}

public enum ActivationDeliveryCapability
{
    Incomplete,
    Complete
}

public sealed record FactStreamSchemaReference(
    string Id,
    int Major,
    int Revision,
    string Hash);

public sealed record FactStreamDescriptor(
    Guid StreamId,
    Guid CollectorInstanceId,
    SubjectReference Subject,
    string OutputId,
    string Source,
    FactKind FactKind,
    FactStreamSchemaReference Schema,
    IReadOnlyDictionary<string, string> Dimensions);

public enum FactRecordState
{
    Present,
    Retracted
}

public record FactTime
{
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public bool? IsFinal { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
}

public sealed record SegmentFactTime : FactTime
{
    public SegmentFactTime(DateTimeOffset start, DateTimeOffset end, bool isFinal)
    {
        Start = start;
        End = end;
        IsFinal = isFinal;
    }
}

public sealed record EventFactTime : FactTime
{
    public EventFactTime(DateTimeOffset occurredAt)
    {
        OccurredAt = occurredAt;
    }
}

public sealed record FactSubmission(
    Guid StreamId,
    int SchemaRevision,
    Guid FactId,
    long Revision,
    DateTimeOffset? ObservedAt,
    FactRecordState RecordState,
    FactTime Time,
    JsonElement Payload);

public enum FactDeliveryStatus
{
    Committed,
    Duplicate,
    Superseded,
    Rejected,
    Retry
}

public sealed record CollectorProtocolError(
    string Code,
    string Message,
    bool Retryable);

public sealed record FactDeliveryOutcome(
    int Index,
    FactDeliveryStatus Status,
    CollectorProtocolError? Error = null,
    int? RetryAfterMilliseconds = null)
{
    public bool IsAcknowledged => Status is
        FactDeliveryStatus.Committed or FactDeliveryStatus.Duplicate or FactDeliveryStatus.Superseded;
}

public sealed record StreamGapReport(
    Guid GapId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Reason,
    int? EstimatedFactsLost = null);

public enum GapDeliveryStatus
{
    Committed,
    Duplicate,
    Rejected,
    Retry
}

public sealed record GapDeliveryOutcome(
    Guid StreamId,
    GapDeliveryStatus Status,
    CollectorProtocolError? Error = null,
    int? RetryAfterMilliseconds = null)
{
    public bool IsAcknowledged => Status is GapDeliveryStatus.Committed or GapDeliveryStatus.Duplicate;
}

public sealed class FactBatchAcknowledgement
{
    internal FactBatchAcknowledgement(
        IReadOnlyList<FactDeliveryOutcome> results,
        CollectorProtocolError? messageError = null)
    {
        Results = results.ToImmutableArray();
        MessageError = messageError;
    }

    public bool IsMessageRejected => MessageError is not null;
    public CollectorProtocolError? MessageError { get; }
    public IReadOnlyList<FactDeliveryOutcome> Results { get; }
}

public sealed class CollectorActivationException(
    CollectorProtocolError error,
    Exception? innerException = null)
    : Exception(error.Message, innerException)
{
    public CollectorProtocolError Error { get; } = error;
}
