using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Collection.CollectorProtocol;

public sealed record CollectorClientDefinition(
    string ArtifactId,
    IReadOnlyDictionary<string, IReadOnlyList<int>> Capabilities,
    string RequiredSubjectKind,
    IReadOnlyList<CollectorOutputBinding> Outputs,
    int OutboxCapacity = 20_000,
    ICollectorClientDiagnostics? Diagnostics = null,
    IReadOnlySet<string>? RequiredCapabilities = null);

public sealed record CollectorOutputBinding(
    string BindingId,
    string OutputId,
    IReadOnlyDictionary<string, string> Dimensions);

public sealed record CollectorRuntimeArtifact(
    string PackageId,
    string PackageVersion,
    string ArtifactId,
    string ArtifactHash);

public sealed record CollectorClientInitialization(
    Guid ActivationId,
    Guid CollectorInstanceId,
    Guid SubjectId,
    string SubjectKind,
    long SpecRevision,
    int ConfigVersion,
    JsonElement Config,
    int MaxFactsPerBatch,
    int MaxBatchBytes,
    string DataDirectory,
    IReadOnlyDictionary<string, int> SelectedCapabilities);

public sealed record CollectorClientStream(
    string BindingId,
    Guid StreamId,
    Guid CollectorInstanceId,
    Guid SubjectId,
    string SubjectKind,
    string OutputId,
    string Source,
    string FactKind,
    string SchemaId,
    int SchemaMajor,
    int SchemaRevision,
    string SchemaHash,
    IReadOnlyDictionary<string, string> Dimensions);

public enum CollectorFactRecordState
{
    Present,
    Retracted
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CollectorSegmentFactTime), "segment")]
[JsonDerivedType(typeof(CollectorEventFactTime), "event")]
public abstract record CollectorFactTime;

public sealed record CollectorSegmentFactTime(
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsFinal) : CollectorFactTime;

public sealed record CollectorEventFactTime(
    DateTimeOffset OccurredAt) : CollectorFactTime;

public sealed record CollectorFact(
    string BindingId,
    int SchemaRevision,
    Guid FactId,
    long Revision,
    DateTimeOffset? ObservedAt,
    CollectorFactRecordState RecordState,
    CollectorFactTime Time,
    JsonElement Payload);

public sealed record BoundCollectorFact(
    Guid StreamId,
    int SchemaRevision,
    Guid FactId,
    long Revision,
    DateTimeOffset? ObservedAt,
    CollectorFactRecordState RecordState,
    CollectorFactTime Time,
    JsonElement Payload);

public enum CollectorFactDeliveryStatus
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

public sealed record CollectorFactDeliveryOutcome(
    int Index,
    CollectorFactDeliveryStatus Status,
    CollectorProtocolError? Error = null,
    int? RetryAfterMilliseconds = null)
{
    public bool IsAcknowledged => Status is
        CollectorFactDeliveryStatus.Committed or
        CollectorFactDeliveryStatus.Duplicate or
        CollectorFactDeliveryStatus.Superseded;
}

public sealed record CollectorFactBatchAcknowledgement(
    IReadOnlyList<CollectorFactDeliveryOutcome> Results,
    CollectorProtocolError? MessageError = null)
{
    public bool IsMessageRejected => MessageError is not null;
}

public sealed record CollectorStreamGap(
    Guid GapId,
    string BindingId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Reason,
    int? EstimatedFactsLost = null);

public enum CollectorGapDeliveryStatus
{
    Committed,
    Duplicate,
    Rejected,
    Retry
}

public sealed record CollectorGapDeliveryOutcome(
    CollectorGapDeliveryStatus Status,
    CollectorProtocolError? Error = null,
    int? RetryAfterMilliseconds = null)
{
    public bool IsAcknowledged => Status is
        CollectorGapDeliveryStatus.Committed or CollectorGapDeliveryStatus.Duplicate;
}

public sealed record CollectorAuthorizationField(
    string Name,
    string Label,
    bool IsSecret,
    string InputMode);

public sealed record CollectorAuthorizationResponse(
    Guid InteractionId,
    IReadOnlyDictionary<string, string> Values);

public sealed record CollectorDrainRequest(Guid RequestMessageId, DateTimeOffset Deadline);

public sealed record CollectorDrainResult(
    long AppliedSpecRevision,
    int PendingFacts,
    int PendingGaps);

public sealed record CollectorDeadLetter(
    DateTimeOffset FailedAt,
    Guid MessageId,
    CollectorFact Fact,
    CollectorProtocolError Error);

public sealed record CollectorClientDiagnostic(
    int DeadLetterCount,
    string DeadLetterPath,
    string? Error = null);

public interface ICollectorClientDiagnostics
{
    void Report(CollectorClientDiagnostic diagnostic);
}
