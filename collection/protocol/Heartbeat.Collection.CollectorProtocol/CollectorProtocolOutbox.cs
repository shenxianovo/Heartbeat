using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Collection.CollectorProtocol;

internal sealed class CollectorProtocolOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly string _deadLetterPath;
    private readonly string _gapDeadLetterPath;
    private readonly int _capacity;
    private readonly Func<string, string, bool> _publishDurableFile;
    private OutboxState _state;
    private List<CollectorDeadLetter> _deadLetters;
    private List<CollectorGapDeadLetter> _gapDeadLetters;
    private bool _dirty;
    private bool _deadLettersDirty;

    private CollectorProtocolOutbox(
        string path,
        int capacity,
        OutboxState state,
        List<CollectorDeadLetter> deadLetters,
        List<CollectorGapDeadLetter> gapDeadLetters,
        Func<string, string, bool> publishDurableFile)
    {
        _path = path;
        _deadLetterPath = Path.Combine(Path.GetDirectoryName(path)!, "collector-protocol-dead-letter.json");
        _gapDeadLetterPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            "collector-protocol-gap-dead-letter.json");
        _capacity = capacity;
        _state = state;
        _deadLetters = deadLetters;
        _gapDeadLetters = gapDeadLetters;
        _publishDurableFile = publishDurableFile;
    }

    public IReadOnlyList<PendingCollectorFact> Facts => _state.Facts;
    public IReadOnlyList<PendingCollectorGap> Gaps => _state.Gaps;
    public PendingCollectorFact? FirstFact => _state.DeliveryOrder.FirstOrDefault() is { } messageId
        ? _state.Facts.FirstOrDefault(item => item.MessageId == messageId)
        : null;
    public PendingCollectorGap? FirstGap => _state.DeliveryOrder.FirstOrDefault() is { } messageId
        ? _state.Gaps.FirstOrDefault(item => item.MessageId == messageId)
        : null;
    public bool HasPending => _state.DeliveryOrder.Count != 0;
    public bool PendingRemainderIsDurable => !_dirty && !_deadLettersDirty;
    public int DeadLetterCount => _deadLetters.Count + _gapDeadLetters.Count;
    public string DeadLetterPath => _deadLetterPath;
    public int GapDeadLetterCount => _gapDeadLetters.Count;
    public string GapDeadLetterPath => _gapDeadLetterPath;

    public static CollectorProtocolOutbox Open(
        string dataDirectory,
        int capacity,
        IReadOnlyList<CollectorOutputBinding> outputs,
        DateTimeOffset now,
        Func<string, string, bool>? publishDurableFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        publishDurableFile ??= PublishUnfenced;
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(Path.GetFullPath(dataDirectory), "collector-protocol-outbox.json");
        var deadLetterPath = Path.Combine(Path.GetDirectoryName(path)!, "collector-protocol-dead-letter.json");
        var gapDeadLetterPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            "collector-protocol-gap-dead-letter.json");
        var state = new OutboxState();
        var migratedCurrentPointGaps = false;
        var migratedDeliveryOrder = false;
        var recoveredCorruptOutbox = false;
        if (File.Exists(path))
        {
            try
            {
                state = ReadEnvelope<OutboxState>(path, "Collector Protocol outbox");
                state = MigrateCurrentPointGaps(state, out migratedCurrentPointGaps);
                state = RestoreLegacyDeliveryOrder(state, out migratedDeliveryOrder);
                Validate(state);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                var quarantine = path + $".corrupt-{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
                var quarantineTemporary = CopyToTemporary(path, quarantine);
                try
                {
                    if (!publishDurableFile(quarantineTemporary, quarantine))
                        throw new OperationCanceledException(
                            "Collector Protocol outbox recovery was fenced before quarantine publication.");
                }
                finally
                {
                    DeleteTemporary(quarantineTemporary);
                }
                var recoveredRange = NonEmptyRange(lastWrite <= now ? lastWrite : now, now);
                state = new OutboxState
                {
                    Gaps = outputs.Select(output => new PendingCollectorGap(
                        Guid.CreateVersion7(),
                        new CollectorStreamGap(
                            Guid.CreateVersion7(),
                            output.BindingId,
                            recoveredRange.Start,
                            recoveredRange.End,
                            "outbox_corrupted"))).ToList()
                };
                state = RestoreLegacyDeliveryOrder(state, out _);
                recoveredCorruptOutbox = true;
            }
        }
        var deadLetters = File.Exists(deadLetterPath)
            ? ReadEnvelope<DeadLetterState>(deadLetterPath, "Collector Protocol dead letter").Entries
            : [];
        var gapDeadLetters = File.Exists(gapDeadLetterPath)
            ? ReadEnvelope<GapDeadLetterState>(gapDeadLetterPath, "Collector Protocol Gap dead letter").Entries
            : [];
        var outbox = new CollectorProtocolOutbox(
            path,
            capacity,
            state,
            deadLetters,
            gapDeadLetters,
            publishDurableFile);
        if (migratedCurrentPointGaps || migratedDeliveryOrder || recoveredCorruptOutbox ||
            !File.Exists(path) && (state.Facts.Count != 0 || state.Gaps.Count != 0))
            outbox.Save();
        return outbox;
    }

    public void BeginActivation()
    {
        if (_state.Facts.Count == 0 && _state.Gaps.Count == 0)
            return;
        var replacements = _state.DeliveryOrder.ToDictionary(
            messageId => messageId,
            _ => Guid.CreateVersion7());
        _state = _state with
        {
            Facts = _state.Facts.Select(item => item with
                { MessageId = replacements[item.MessageId] }).ToList(),
            Gaps = _state.Gaps.Select(item => item with
                { MessageId = replacements[item.MessageId] }).ToList(),
            DeliveryOrder = _state.DeliveryOrder.Select(messageId => replacements[messageId]).ToList()
        };
        _dirty = true;
        Save();
    }

    public void Enqueue(CollectorFact fact) => EnqueueFacts([fact]);

    public void EnqueueFacts(IReadOnlyList<CollectorFact> incomingFacts)
    {
        _state = PrepareEnqueuedFacts(incomingFacts);
        _dirty = true;
        Save();
    }

    public CollectorAdmissionOutcome EnqueueFacts(
        IReadOnlyList<CollectorFact> incomingFacts,
        CollectorAdmissionLease admission) =>
        SaveAdmissionState(PrepareEnqueuedFacts(incomingFacts), admission);

    private OutboxState PrepareEnqueuedFacts(IReadOnlyList<CollectorFact> incomingFacts)
    {
        ArgumentNullException.ThrowIfNull(incomingFacts);
        var facts = _state.Facts.ToList();
        var gaps = _state.Gaps.ToList();
        var deliveryOrder = _state.DeliveryOrder.ToList();
        foreach (var fact in incomingFacts)
        {
            ArgumentNullException.ThrowIfNull(fact);
            var index = facts.FindIndex(item =>
                item.Fact.BindingId == fact.BindingId && item.Fact.FactId == fact.FactId);
            if (index >= 0)
            {
                if (facts[index].Fact.Revision < fact.Revision)
                {
                    var replacement = new PendingCollectorFact(Guid.CreateVersion7(), fact);
                    ReplaceDeliveryId(deliveryOrder, facts[index].MessageId, replacement.MessageId);
                    facts[index] = replacement;
                }
            }
            else
            {
                var pending = new PendingCollectorFact(Guid.CreateVersion7(), fact);
                facts.Add(pending);
                deliveryOrder.Add(pending.MessageId);
            }

            while (facts.Count > _capacity)
            {
                var evicted = facts[0].Fact;
                var evictedMessageId = facts[0].MessageId;
                facts.RemoveAt(0);
                var (start, end) = FactRange(evicted);
                var pendingGap = new PendingCollectorGap(
                    Guid.CreateVersion7(),
                    new CollectorStreamGap(
                        Guid.CreateVersion7(),
                        evicted.BindingId,
                        start,
                        end,
                        "outbox_capacity_exceeded",
                        1));
                gaps.Add(pendingGap);
                ReplaceDeliveryId(deliveryOrder, evictedMessageId, pendingGap.MessageId);
            }
        }
        return _state with { Facts = facts, Gaps = gaps, DeliveryOrder = deliveryOrder };
    }

    public void EnqueueGap(CollectorStreamGap gap)
    {
        ArgumentNullException.ThrowIfNull(gap);
        if (_state.Gaps.Any(item => item.Gap.GapId == gap.GapId))
        {
            if (_dirty)
                Save();
            return;
        }
        var pending = new PendingCollectorGap(Guid.CreateVersion7(), gap);
        _state.Gaps.Add(pending);
        _state.DeliveryOrder.Add(pending.MessageId);
        _dirty = true;
        Save();
    }

    public CollectorAdmissionOutcome EnqueueGap(
        CollectorStreamGap gap,
        CollectorAdmissionLease admission)
    {
        ArgumentNullException.ThrowIfNull(gap);
        if (_state.Gaps.Any(item => item.Gap.GapId == gap.GapId))
            return _dirty
                ? SaveAdmissionState(_state, admission)
                : CollectorAdmissionOutcome.Committed;
        var pending = new PendingCollectorGap(Guid.CreateVersion7(), gap);
        return SaveAdmissionState(_state with
        {
            Gaps = [.. _state.Gaps, pending],
            DeliveryOrder = [.. _state.DeliveryOrder, pending.MessageId]
        }, admission);
    }

    public CollectorDeliveryCommitOutcome AcknowledgeFact(
        Guid messageId,
        CollectorDeliveryLease delivery)
    {
        var facts = _state.Facts.Where(item => item.MessageId != messageId).ToList();
        if (facts.Count == _state.Facts.Count)
            return CollectorDeliveryCommitOutcome.Committed;
        return SaveDeliveryState(_state with
        {
            Facts = facts,
            DeliveryOrder = _state.DeliveryOrder.Where(id => id != messageId).ToList()
        }, delivery);
    }

    public CollectorDeliveryCommitOutcome RetryFact(
        Guid messageId,
        CollectorDeliveryLease delivery)
    {
        var index = _state.Facts.FindIndex(item => item.MessageId == messageId);
        if (index < 0)
            return CollectorDeliveryCommitOutcome.Committed;
        var facts = _state.Facts.ToList();
        var replacementId = Guid.CreateVersion7();
        facts[index] = facts[index] with { MessageId = replacementId };
        var order = _state.DeliveryOrder.ToList();
        ReplaceDeliveryId(order, messageId, replacementId);
        return SaveDeliveryState(_state with { Facts = facts, DeliveryOrder = order }, delivery);
    }

    public CollectorDeliveryCommitOutcome AcknowledgeGap(
        Guid messageId,
        CollectorDeliveryLease delivery)
    {
        var gaps = _state.Gaps.Where(item => item.MessageId != messageId).ToList();
        if (gaps.Count == _state.Gaps.Count)
            return CollectorDeliveryCommitOutcome.Committed;
        return SaveDeliveryState(_state with
        {
            Gaps = gaps,
            DeliveryOrder = _state.DeliveryOrder.Where(id => id != messageId).ToList()
        }, delivery);
    }

    public CollectorDeliveryCommitOutcome RetryGap(
        Guid messageId,
        CollectorDeliveryLease delivery)
    {
        var index = _state.Gaps.FindIndex(item => item.MessageId == messageId);
        if (index < 0)
            return CollectorDeliveryCommitOutcome.Committed;
        var gaps = _state.Gaps.ToList();
        var replacementId = Guid.CreateVersion7();
        gaps[index] = gaps[index] with { MessageId = replacementId };
        var order = _state.DeliveryOrder.ToList();
        ReplaceDeliveryId(order, messageId, replacementId);
        return SaveDeliveryState(_state with { Gaps = gaps, DeliveryOrder = order }, delivery);
    }

    public CollectorDeliveryCommitOutcome DeadLetter(
        PendingCollectorFact pending,
        CollectorProtocolError error,
        DateTimeOffset now,
        CollectorDeliveryLease delivery)
    {
        var nextDeadLetters = _deadLetters.ToList();
        nextDeadLetters.Add(new CollectorDeadLetter(now, pending.MessageId, pending.Fact, error));
        var nextState = _state with
        {
            Facts = _state.Facts.Where(item => item.MessageId != pending.MessageId).ToList(),
            DeliveryOrder = _state.DeliveryOrder.Where(id => id != pending.MessageId).ToList()
        };
        var deadLetterTemporary = WriteEnvelopeTemporary(
            _deadLetterPath,
            new DeadLetterState { Entries = nextDeadLetters });
        var outboxTemporary = WriteEnvelopeTemporary(_path, nextState);
        try
        {
            return delivery.TryCommit(() =>
            {
                File.Move(deadLetterTemporary, _deadLetterPath, overwrite: true);
                File.Move(outboxTemporary, _path, overwrite: true);
                _deadLetters = nextDeadLetters;
                _state = nextState;
                _deadLettersDirty = false;
                _dirty = false;
            });
        }
        finally
        {
            DeleteTemporary(deadLetterTemporary);
            DeleteTemporary(outboxTemporary);
        }
    }

    public CollectorDeliveryCommitOutcome DeadLetter(
        PendingCollectorGap pending,
        CollectorProtocolError error,
        DateTimeOffset now,
        CollectorDeliveryLease delivery)
    {
        var nextGapDeadLetters = _gapDeadLetters.ToList();
        nextGapDeadLetters.Add(new CollectorGapDeadLetter(now, pending.MessageId, pending.Gap, error));
        var nextState = _state with
        {
            Gaps = _state.Gaps.Where(item => item.MessageId != pending.MessageId).ToList(),
            DeliveryOrder = _state.DeliveryOrder.Where(id => id != pending.MessageId).ToList()
        };
        var deadLetterTemporary = WriteEnvelopeTemporary(
            _gapDeadLetterPath,
            new GapDeadLetterState { Entries = nextGapDeadLetters });
        var outboxTemporary = WriteEnvelopeTemporary(_path, nextState);
        try
        {
            return delivery.TryCommit(() =>
            {
                File.Move(deadLetterTemporary, _gapDeadLetterPath, overwrite: true);
                File.Move(outboxTemporary, _path, overwrite: true);
                _gapDeadLetters = nextGapDeadLetters;
                _state = nextState;
                _deadLettersDirty = false;
                _dirty = false;
            });
        }
        finally
        {
            DeleteTemporary(deadLetterTemporary);
            DeleteTemporary(outboxTemporary);
        }
    }

    public void PersistPending() => Save();

    private void Save()
    {
        if (_deadLettersDirty)
        {
            SaveEnvelope(
                _deadLetterPath,
                new DeadLetterState { Entries = _deadLetters });
            _deadLettersDirty = false;
        }
        SaveEnvelope(_path, _state);
        _dirty = false;
    }

    private static T ReadEnvelope<T>(string path, string description) where T : class
    {
        var envelope = JsonSerializer.Deserialize<StateEnvelope<T>>(
            File.ReadAllText(path, Encoding.UTF8),
            JsonOptions) ?? throw new InvalidDataException($"{description} is empty.");
        if (envelope.SchemaVersion != 1 || envelope.State is null)
            throw new InvalidDataException($"{description} has an unsupported schemaVersion.");
        return envelope.State;
    }

    private void SaveEnvelope<T>(string path, T state)
    {
        var temporary = WriteEnvelopeTemporary(path, state);
        try
        {
            if (!_publishDurableFile(temporary, path))
                throw new OperationCanceledException(
                    "Collector Protocol durable mutation was fenced before publication.");
        }
        finally
        {
            DeleteTemporary(temporary);
        }
    }

    private CollectorDeliveryCommitOutcome SaveDeliveryState(
        OutboxState nextState,
        CollectorDeliveryLease delivery)
    {
        var temporary = WriteEnvelopeTemporary(_path, nextState);
        try
        {
            return delivery.TryCommit(() =>
            {
                File.Move(temporary, _path, overwrite: true);
                _state = nextState;
                _dirty = false;
            });
        }
        finally
        {
            DeleteTemporary(temporary);
        }
    }

    private CollectorAdmissionOutcome SaveAdmissionState(
        OutboxState nextState,
        CollectorAdmissionLease admission)
    {
        var temporary = WriteEnvelopeTemporary(_path, nextState);
        try
        {
            try
            {
                return admission.TryCommit(() =>
                {
                    if (!_publishDurableFile(temporary, _path))
                        return false;
                    _state = nextState;
                    _dirty = false;
                    return true;
                });
            }
            catch (IOException)
            {
                // Preserve the observed, non-durable remainder in memory while the same
                // admission lease retries publication. A drain may supersede that lease,
                // but it must still report the known remainder truthfully.
                _state = nextState;
                _dirty = true;
                throw;
            }
        }
        finally
        {
            DeleteTemporary(temporary);
        }
    }

    private static string WriteEnvelopeTemporary<T>(string path, T state)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Collector Protocol state path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(new StateEnvelope<T>(1, state), JsonOptions),
            new UTF8Encoding(false));
        return temporary;
    }

    private static string CopyToTemporary(string sourcePath, string destinationPath)
    {
        var temporary = destinationPath + $".{Guid.NewGuid():N}.tmp";
        File.Copy(sourcePath, temporary);
        return temporary;
    }

    private static void DeleteTemporary(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A stale uniquely-named temporary file is harmless and can be cleaned externally.
        }
    }

    private static bool PublishUnfenced(string preparedPath, string authoritativePath)
    {
        File.Move(preparedPath, authoritativePath, overwrite: true);
        return true;
    }

    private static void Validate(OutboxState state)
    {
        if (state.Facts is null || state.Gaps is null || state.DeliveryOrder is null)
            throw new InvalidDataException("Collector Protocol outbox contains invalid state.");
        var messageIds = state.Facts.Select(item => item.MessageId)
            .Concat(state.Gaps.Select(item => item.MessageId))
            .ToArray();
        if (state.Facts.Any(item => item.MessageId == Guid.Empty || item.Fact.FactId == Guid.Empty ||
                                    item.Fact.Revision <= 0 || string.IsNullOrWhiteSpace(item.Fact.BindingId)) ||
            state.Gaps.Any(item => item.MessageId == Guid.Empty || item.Gap.GapId == Guid.Empty ||
                                   item.Gap.End <= item.Gap.Start || string.IsNullOrWhiteSpace(item.Gap.BindingId)) ||
            messageIds.Length != messageIds.Distinct().Count() ||
            state.DeliveryOrder.Count != messageIds.Length ||
            !messageIds.ToHashSet().SetEquals(state.DeliveryOrder))
            throw new InvalidDataException("Collector Protocol outbox contains invalid state.");
    }

    private static OutboxState RestoreLegacyDeliveryOrder(OutboxState state, out bool migrated)
    {
        if (state.Facts is null || state.Gaps is null || state.DeliveryOrder is null)
            throw new InvalidDataException("Collector Protocol outbox contains invalid state.");
        if (state.DeliveryOrder.Count != 0 || state.Facts.Count == 0 && state.Gaps.Count == 0)
        {
            migrated = false;
            return state;
        }
        migrated = true;
        return state with
        {
            // Schema v1 stored the two families separately and flushed Facts before Gaps. Preserve
            // that exact legacy behavior; only current writes can retain their true interleaving.
            DeliveryOrder = state.Facts.Select(item => item.MessageId)
                .Concat(state.Gaps.Select(item => item.MessageId))
                .ToList()
        };
    }

    private static void ReplaceDeliveryId(List<Guid> order, Guid current, Guid replacement)
    {
        var index = order.IndexOf(current);
        if (index < 0)
            throw new InvalidDataException("Collector Protocol outbox delivery order is incomplete.");
        order[index] = replacement;
    }

    private static OutboxState MigrateCurrentPointGaps(OutboxState state, out bool migrated)
    {
        // Schema v1 emitted point Gaps for evicted Event facts. Removal requires the supported
        // data-directory inventory and offline/rollback evidence in compatibility-debt.md.
        var changed = false;
        var gaps = state.Gaps.Select(item =>
        {
            if (item.Gap.End != item.Gap.Start || item.Gap.Start == DateTimeOffset.MaxValue)
                return item;
            changed = true;
            return item with { Gap = item.Gap with { End = item.Gap.Start.AddTicks(1) } };
        }).ToList();
        migrated = changed;
        return migrated ? state with { Gaps = gaps } : state;
    }

    private static (DateTimeOffset Start, DateTimeOffset End) FactRange(CollectorFact fact) => fact.Time switch
    {
        CollectorSegmentFactTime segment => NonEmptyRange(segment.Start, segment.End),
        CollectorEventFactTime occurrence => NonEmptyRange(occurrence.OccurredAt, occurrence.OccurredAt),
        _ => throw new InvalidOperationException("Unknown Collector Fact time shape.")
    };

    private static (DateTimeOffset Start, DateTimeOffset End) NonEmptyRange(
        DateTimeOffset start,
        DateTimeOffset end)
    {
        if (end < start)
            throw new InvalidDataException("Collector Fact range ends before it starts.");
        if (end > start)
            return (start, end);
        if (start == DateTimeOffset.MaxValue)
            throw new InvalidDataException("Collector Fact point cannot be represented as a half-open Gap range.");
        return (start, start.AddTicks(1));
    }

    private sealed record StateEnvelope<T>(int SchemaVersion, T State);

    private sealed record OutboxState
    {
        public List<PendingCollectorFact> Facts { get; init; } = [];
        public List<PendingCollectorGap> Gaps { get; init; } = [];
        public List<Guid> DeliveryOrder { get; init; } = [];
    }


    private sealed record DeadLetterState
    {
        public List<CollectorDeadLetter> Entries { get; init; } = [];
    }

    private sealed record GapDeadLetterState
    {
        public List<CollectorGapDeadLetter> Entries { get; init; } = [];
    }
}

internal sealed record PendingCollectorFact(Guid MessageId, CollectorFact Fact);
internal sealed record PendingCollectorGap(Guid MessageId, CollectorStreamGap Gap);
internal sealed record CollectorGapDeadLetter(
    DateTimeOffset FailedAt,
    Guid MessageId,
    CollectorStreamGap Gap,
    CollectorProtocolError Error);
