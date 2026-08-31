using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;

namespace Heartbeat.Collection.Hub.Collectors.Protocol;

/// <summary>
/// Transport-neutral Collector Protocol session. The session owns protocol ordering,
/// per-Activation serialization and request replay; its delegates own durable Hub commits.
/// </summary>
internal sealed class CollectorActivationSession
{
    private readonly object _gate = new();
    private readonly ActivationDeliveryFence _deliveryFence;
    private readonly CollectorProtocolLimits _limits;
    private readonly Func<Guid, IReadOnlyList<FactSubmission>, FactBatchAcknowledgement> _commitFacts;
    private readonly Func<Guid, StreamGapReport, GapDeliveryOutcome> _commitGap;
    private readonly Action<Guid, IReadOnlyList<FactDeliveryOutcome>> _markAcknowledgedTraffic;
    private readonly Dictionary<Guid, MessageAttemptIdentity> _messageAttempts = [];
    private readonly Dictionary<Guid, PublishReplay> _publishReplays = [];
    private readonly Dictionary<Guid, GapReplay> _gapReplays = [];
    private readonly List<CollectorHandshakeStep> _handshakeTranscript = [CollectorHandshakeStep.Hello];
    private CollectorHandshakeStep _lastHandshakeStep = CollectorHandshakeStep.Hello;
    private CollectorActivationState _state;
    private int _deadlineFenced;
    private int _releaseCompleted;
    private ImmutableDictionary<string, FactStreamDescriptor> _streams =
        ImmutableDictionary<string, FactStreamDescriptor>.Empty.WithComparers(StringComparer.Ordinal);

    internal CollectorActivationSession(
        Guid activationId,
        Guid helloMessageId,
        LocalCollectorPackage package,
        CollectorProtocolLimits limits,
        ActivationDeliveryCapability deliveryCapability,
        ActivationDeliveryFence deliveryFence,
        Func<Guid, IReadOnlyList<FactSubmission>, FactBatchAcknowledgement> commitFacts,
        Func<Guid, StreamGapReport, GapDeliveryOutcome> commitGap,
        Action<Guid, IReadOnlyList<FactDeliveryOutcome>> markAcknowledgedTraffic)
    {
        ActivationId = activationId;
        HelloMessageId = helloMessageId;
        Package = package;
        _limits = limits;
        DeliveryCapability = deliveryCapability;
        _deliveryFence = deliveryFence;
        _commitFacts = commitFacts;
        _commitGap = commitGap;
        _markAcknowledgedTraffic = markAcknowledgedTraffic;
        _messageAttempts.Add(
            helloMessageId,
            new MessageAttemptIdentity("activation.hello", "accepted"));
        _state = CollectorActivationState.Negotiating;
    }

    public Guid ActivationId { get; }
    public Guid HelloMessageId { get; }
    public CollectorActivationState State => _deliveryFence.IsFenced
        ? CollectorActivationState.Stopped
        : _state;
    public ExternalHostActivationStopReason? StopReason { get; private set; }
    public ActivationDeliveryCapability DeliveryCapability { get; }
    public IReadOnlyList<CollectorHandshakeStep> HandshakeTranscript
    {
        get { lock (_gate) return _handshakeTranscript.ToImmutableArray(); }
    }
    public IReadOnlyDictionary<string, FactStreamDescriptor> Streams => _streams;
    internal LocalCollectorPackage Package { get; }
    internal ICollectorDurableCommitFence DurableCommitFence => _deliveryFence;

    internal void AcceptInitialized(long appliedSpecRevision, long expectedSpecRevision)
    {
        lock (_gate)
        {
            RequireHandshakeStep(CollectorHandshakeStep.Hello, "activation.initialized");
            if (appliedSpecRevision != expectedSpecRevision)
                throw ActivationError("spec_revision_stale", "Collector did not apply the current SpecRevision.");
            AdvanceHandshake(CollectorHandshakeStep.Initialize);
        }
    }

    internal void AcceptStreams(IReadOnlyDictionary<string, FactStreamDescriptor> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        lock (_gate)
        {
            RequireHandshakeStep(CollectorHandshakeStep.Initialize, "streams.open");
            _streams = streams.ToImmutableDictionary(StringComparer.Ordinal);
            AdvanceHandshake(CollectorHandshakeStep.StreamsOpen);
            _state = CollectorActivationState.OpeningStreams;
        }
    }

    internal void AcceptReady(long appliedSpecRevision, long expectedSpecRevision, Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_gate)
        {
            RequireHandshakeStep(CollectorHandshakeStep.StreamsOpen, "activation.ready");
            if (appliedSpecRevision != expectedSpecRevision)
                throw ActivationError("spec_revision_stale", "Collector did not apply the current SpecRevision.");
            commit();
            AdvanceHandshake(CollectorHandshakeStep.Ready);
            _state = CollectorActivationState.Ready;
        }
    }

    public ValueTask<FactBatchAcknowledgement> PublishAsync(
        Guid streamId,
        Guid messageId,
        IReadOnlyList<FactSubmission> facts,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDeliveryFencedAfterDeadline();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(facts);
        var snapshot = facts.ToImmutableArray();
        lock (_gate)
        {
            ThrowIfDeliveryFencedAfterDeadline();
            if (!IsUuidV7(messageId))
                return ValueTask.FromResult(MessageRejected(
                    "protocol_invalid_message",
                    "facts.publish messageId must be a UUIDv7."));
            if (snapshot.Any(fact => fact is null || fact.Time is null ||
                                     !Enum.IsDefined(fact.RecordState) ||
                                     fact.RecordState == FactRecordState.Present &&
                                     fact.Payload.ValueKind == JsonValueKind.Undefined))
            {
                RegisterAttempt(messageId, "facts.publish", "invalid:fact-shape");
                return ValueTask.FromResult(MessageRejected(
                    "protocol_invalid_message",
                    "facts.publish contains an incomplete FactSubmission."));
            }

            var payloadJsonError = snapshot
                .Where(fact => fact.Payload.ValueKind != JsonValueKind.Undefined)
                .Select(fact => FactCanonicalization.ValidateProtocolJson(fact.Payload))
                .FirstOrDefault(error => error is not null);
            if (payloadJsonError is not null)
            {
                RegisterAttempt(messageId, "facts.publish", "invalid:payload-json");
                return ValueTask.FromResult(MessageRejected(
                    "protocol_invalid_message",
                    $"facts.publish payload is not valid protocol JSON: {payloadJsonError}"));
            }

            long logicalMessageSize;
            string requestHash;
            try
            {
                requestHash = FactCanonicalization.PublishRequestHash(snapshot);
                logicalMessageSize = FactCanonicalization.PublishLogicalMessageSize(
                    ActivationId,
                    messageId,
                    snapshot);
            }
            catch (Exception exception) when (exception is
                InvalidOperationException or FormatException or OverflowException)
            {
                RegisterAttempt(messageId, "facts.publish", "invalid:canonical-value");
                return ValueTask.FromResult(MessageRejected(
                    "protocol_invalid_message",
                    "facts.publish contains a value that cannot be canonically represented."));
            }

            if (!RegisterAttempt(messageId, "facts.publish", requestHash))
                return ValueTask.FromResult(MessageRejected(
                    "protocol_invalid_message",
                    "The same messageId was reused for another protocol request."));
            if (snapshot.Length == 0 || snapshot.Length > _limits.MaxFactsPerBatch)
                return ValueTask.FromResult(MessageRejected(
                    "batch_limit_exceeded",
                    $"facts.publish must contain between 1 and {_limits.MaxFactsPerBatch} Facts."));
            if (logicalMessageSize > _limits.MaxBatchBytes)
                return ValueTask.FromResult(MessageRejected(
                    "batch_limit_exceeded",
                    $"facts.publish exceeds the negotiated {_limits.MaxBatchBytes}-byte logical message limit."));
            if (_publishReplays.TryGetValue(messageId, out var replay))
            {
                if (replay.RequestHash != requestHash)
                    return ValueTask.FromResult(MessageRejected(
                        "protocol_invalid_message",
                        "The same messageId was reused with different facts.publish content."));
                replay.Error?.Throw();
                _markAcknowledgedTraffic(streamId, replay.Outcome!.Results);
                return ValueTask.FromResult(replay.Outcome);
            }
            if (snapshot.Any(fact => fact.StreamId != streamId) ||
                snapshot.Select(fact => (fact.StreamId, fact.FactId)).Distinct().Count() != snapshot.Length)
            {
                var rejected = MessageRejected(
                    "batch_limit_exceeded",
                    "facts.publish contains an unexpected StreamId or duplicate (StreamId, FactId).");
                _publishReplays.Add(messageId, new PublishReplay(requestHash, rejected, null));
                return ValueTask.FromResult(rejected);
            }

            try
            {
                var acknowledgement = _commitFacts(streamId, snapshot);
                ThrowIfDeliveryFencedAfterDeadline();
                if (!acknowledgement.Results.Any(result => result.Status == FactDeliveryStatus.Retry))
                    _publishReplays.Add(messageId, new PublishReplay(requestHash, acknowledgement, null));
                return ValueTask.FromResult(acknowledgement);
            }
            catch (OperationCanceledException) when (IsDeliveryFencedAfterDeadline())
            {
                throw;
            }
            catch (Exception exception)
            {
                _publishReplays.Add(
                    messageId,
                    new PublishReplay(requestHash, null, ExceptionDispatchInfo.Capture(exception)));
                throw;
            }
        }
    }

    public ValueTask<GapDeliveryOutcome> ReportGapAsync(
        Guid streamId,
        Guid messageId,
        StreamGapReport gap,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDeliveryFencedAfterDeadline();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(gap);
        lock (_gate)
        {
            ThrowIfDeliveryFencedAfterDeadline();
            var requestHash = GapRequestHash(streamId, gap);
            if (IsUuidV7(messageId) && !RegisterAttempt(messageId, "stream.gap", requestHash))
                return ValueTask.FromResult(GapRejected(
                    streamId,
                    "protocol_invalid_message",
                    "The same messageId was reused for another protocol request."));
            if (_gapReplays.TryGetValue(messageId, out var replay))
                return ValueTask.FromResult(replay.RequestHash == requestHash
                    ? replay.Outcome
                    : GapRejected(
                        streamId,
                        "protocol_invalid_message",
                        "The same messageId was reused with different stream.gap content."));

            GapDeliveryOutcome outcome;
            if (!IsUuidV7(messageId) || !IsUuidV7(gap.GapId) ||
                gap.Start.Offset != TimeSpan.Zero || gap.End.Offset != TimeSpan.Zero || gap.End <= gap.Start ||
                string.IsNullOrWhiteSpace(gap.Reason) || !IsSnakeCaseCode(gap.Reason) ||
                gap.EstimatedFactsLost is <= 0)
            {
                outcome = GapRejected(
                    streamId,
                    "protocol_invalid_message",
                    "stream.gap contains invalid identity, time, reason, or estimate.");
            }
            else
            {
                outcome = _commitGap(streamId, gap);
                ThrowIfDeliveryFencedAfterDeadline();
            }
            if (IsUuidV7(messageId) && outcome.Status != GapDeliveryStatus.Retry)
                _gapReplays[messageId] = new GapReplay(requestHash, outcome);
            return ValueTask.FromResult(outcome);
        }
    }

    internal bool BeginDrain()
    {
        // Publish/Gap delivery may be executing projection code outside our control. Stop must be
        // able to establish its hard deadline without waiting for that synchronous invocation.
        if (!Monitor.TryEnter(_gate))
            return true;
        try
        {
            if (_state == CollectorActivationState.Stopped)
                return false;
            if (_state != CollectorActivationState.Draining)
                _state = CollectorActivationState.Draining;
            return true;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    internal void FenceDeliveryAfterDeadline()
    {
        Volatile.Write(ref _deadlineFenced, 1);
        _deliveryFence.Fence();
    }

    internal bool TryCommitAcknowledgement(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return _deliveryFence.TryCommitHost(commit);
    }

    internal bool TryCompleteStop(Action release, ExternalHostActivationStopReason? reason = null)
    {
        if (!Monitor.TryEnter(_gate))
            return false;
        try
        {
            CompleteStopLocked(release, reason);
            return true;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    internal void CompleteStopAfterDeadline(
        Action release,
        ExternalHostActivationStopReason? reason = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!_deliveryFence.IsFenced)
            throw new InvalidOperationException("The Activation must be fenced before forced deadline release.");
        if (Interlocked.Exchange(ref _releaseCompleted, 1) == 0)
            release();
        StopReason = reason;
    }

    internal void CompleteStop(Action release, ExternalHostActivationStopReason? reason = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        lock (_gate)
            CompleteStopLocked(release, reason);
    }

    private void CompleteStopLocked(Action release, ExternalHostActivationStopReason? reason)
    {
        if (_state == CollectorActivationState.Stopped && Volatile.Read(ref _releaseCompleted) != 0)
            return;
        _state = CollectorActivationState.Draining;
        _deliveryFence.Fence();
        if (Interlocked.Exchange(ref _releaseCompleted, 1) == 0)
            release();
        StopReason = reason;
        _state = CollectorActivationState.Stopped;
        _messageAttempts.Clear();
        _publishReplays.Clear();
        _gapReplays.Clear();
    }

    private bool IsDeliveryFencedAfterDeadline() => Volatile.Read(ref _deadlineFenced) != 0;

    private void ThrowIfDeliveryFencedAfterDeadline()
    {
        if (IsDeliveryFencedAfterDeadline())
        {
            throw new OperationCanceledException(
                "Collector delivery was fenced after the Activation drain deadline.");
        }
    }

    private void RequireHandshakeStep(CollectorHandshakeStep expected, string next)
    {
        if (_state is CollectorActivationState.Draining or CollectorActivationState.Stopped ||
            IsDeliveryFencedAfterDeadline())
            throw ActivationError(
                "activation_stopping",
                $"Collector Activation is stopping before '{next}'.");
        if (_lastHandshakeStep != expected)
            throw ActivationError(
                "protocol_invalid_message",
                $"Collector Protocol expected '{expected}' before '{next}'.");
    }

    private void AdvanceHandshake(CollectorHandshakeStep next)
    {
        _lastHandshakeStep = next;
        _handshakeTranscript.Add(next);
    }

    private bool RegisterAttempt(Guid messageId, string messageType, string requestHash)
    {
        if (_messageAttempts.TryGetValue(messageId, out var existing))
            return existing.MessageType == messageType && existing.RequestHash == requestHash;
        _messageAttempts.Add(messageId, new MessageAttemptIdentity(messageType, requestHash));
        return true;
    }

    private static FactBatchAcknowledgement MessageRejected(string code, string message) =>
        new([], new CollectorProtocolError(code, message, false));

    private static GapDeliveryOutcome GapRejected(Guid streamId, string code, string message) =>
        new(streamId, GapDeliveryStatus.Rejected, new CollectorProtocolError(code, message, false));

    private static CollectorActivationException ActivationError(string code, string message) =>
        new(new CollectorProtocolError(code, message, false));

    private static bool IsUuidV7(Guid value) => value != Guid.Empty && value.Version == 7;

    private static bool IsSnakeCaseCode(string value) =>
        value.Length is > 0 and <= 64 &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static string GapRequestHash(Guid streamId, StreamGapReport gap)
    {
        var canonical = string.Join(
            "\n",
            streamId.ToString("D"),
            gap.GapId.ToString("D"),
            gap.Start.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            gap.End.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            gap.Reason,
            gap.EstimatedFactsLost?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record MessageAttemptIdentity(string MessageType, string RequestHash);
    private sealed record PublishReplay(
        string RequestHash,
        FactBatchAcknowledgement? Outcome,
        ExceptionDispatchInfo? Error);
    private sealed record GapReplay(string RequestHash, GapDeliveryOutcome Outcome);
}
