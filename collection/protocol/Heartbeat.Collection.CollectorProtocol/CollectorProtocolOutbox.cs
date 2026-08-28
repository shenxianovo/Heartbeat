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
    private readonly int _capacity;
    private OutboxState _state;
    private List<CollectorDeadLetter> _deadLetters;
    private bool _dirty;
    private bool _deadLettersDirty;

    private CollectorProtocolOutbox(
        string path,
        int capacity,
        OutboxState state,
        List<CollectorDeadLetter> deadLetters)
    {
        _path = path;
        _deadLetterPath = Path.Combine(Path.GetDirectoryName(path)!, "collector-protocol-dead-letter.json");
        _capacity = capacity;
        _state = state;
        _deadLetters = deadLetters;
    }

    public IReadOnlyList<PendingCollectorFact> Facts => _state.Facts;
    public IReadOnlyList<PendingCollectorGap> Gaps => _state.Gaps;
    public int DeadLetterCount => _deadLetters.Count;
    public string DeadLetterPath => _deadLetterPath;

    public static CollectorProtocolOutbox Open(
        string dataDirectory,
        int capacity,
        IReadOnlyList<CollectorOutputBinding> outputs,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(Path.GetFullPath(dataDirectory), "collector-protocol-outbox.json");
        var deadLetterPath = Path.Combine(Path.GetDirectoryName(path)!, "collector-protocol-dead-letter.json");
        var state = new OutboxState();
        if (File.Exists(path))
        {
            try
            {
                state = ReadEnvelope<OutboxState>(path, "Collector Protocol outbox");
                Validate(state);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                var quarantine = path + $".corrupt-{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
                File.Move(path, quarantine);
                state = new OutboxState
                {
                    Gaps = outputs.Select(output => new PendingCollectorGap(
                        Guid.CreateVersion7(),
                        new CollectorStreamGap(
                            Guid.CreateVersion7(),
                            output.BindingId,
                            lastWrite <= now ? lastWrite : now,
                            now,
                            "outbox_corrupted"))).ToList()
                };
            }
        }
        var deadLetters = File.Exists(deadLetterPath)
            ? ReadEnvelope<DeadLetterState>(deadLetterPath, "Collector Protocol dead letter").Entries
            : [];
        var outbox = new CollectorProtocolOutbox(path, capacity, state, deadLetters);
        if (!File.Exists(path) && (state.Facts.Count != 0 || state.Gaps.Count != 0))
            outbox.Save();
        return outbox;
    }

    public void BeginActivation()
    {
        if (_state.Facts.Count == 0 && _state.Gaps.Count == 0)
            return;
        _state = _state with
        {
            Facts = _state.Facts.Select(item => item with { MessageId = Guid.CreateVersion7() }).ToList(),
            Gaps = _state.Gaps.Select(item => item with { MessageId = Guid.CreateVersion7() }).ToList()
        };
        _dirty = true;
        Save();
    }

    public void Enqueue(CollectorFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var facts = _state.Facts.ToList();
        var index = facts.FindIndex(item =>
            item.Fact.BindingId == fact.BindingId && item.Fact.FactId == fact.FactId);
        if (index >= 0)
        {
            if (facts[index].Fact.Revision >= fact.Revision)
            {
                if (_dirty)
                    Save();
                return;
            }
            facts[index] = new PendingCollectorFact(Guid.CreateVersion7(), fact);
        }
        else
        {
            facts.Add(new PendingCollectorFact(Guid.CreateVersion7(), fact));
        }

        var gaps = _state.Gaps.ToList();
        while (facts.Count > _capacity)
        {
            var evicted = facts[0].Fact;
            facts.RemoveAt(0);
            var (start, end) = FactRange(evicted);
            gaps.Add(new PendingCollectorGap(
                Guid.CreateVersion7(),
                new CollectorStreamGap(
                    Guid.CreateVersion7(),
                    evicted.BindingId,
                    start,
                    end,
                    "outbox_capacity_exceeded",
                    1)));
        }
        _state = _state with { Facts = facts, Gaps = gaps };
        _dirty = true;
        Save();
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
        _state.Gaps.Add(new PendingCollectorGap(Guid.CreateVersion7(), gap));
        _dirty = true;
        Save();
    }

    public void AcknowledgeFact(Guid messageId)
    {
        _state.Facts.RemoveAll(item => item.MessageId == messageId);
        _dirty = true;
        Save();
    }

    public void RetryFact(Guid messageId)
    {
        var index = _state.Facts.FindIndex(item => item.MessageId == messageId);
        if (index < 0)
            return;
        _state.Facts[index] = _state.Facts[index] with { MessageId = Guid.CreateVersion7() };
        _dirty = true;
        Save();
    }

    public void AcknowledgeGap(Guid messageId)
    {
        _state.Gaps.RemoveAll(item => item.MessageId == messageId);
        _dirty = true;
        Save();
    }

    public void RetryGap(Guid messageId)
    {
        var index = _state.Gaps.FindIndex(item => item.MessageId == messageId);
        if (index < 0)
            return;
        _state.Gaps[index] = _state.Gaps[index] with { MessageId = Guid.CreateVersion7() };
        _dirty = true;
        Save();
    }

    public void DeadLetter(PendingCollectorFact pending, CollectorProtocolError error, DateTimeOffset now)
    {
        var nextDeadLetters = _deadLetters.ToList();
        nextDeadLetters.Add(new CollectorDeadLetter(now, pending.MessageId, pending.Fact, error));
        _deadLetters = nextDeadLetters;
        _deadLettersDirty = true;
        _state.Facts.RemoveAll(item => item.MessageId == pending.MessageId);
        _dirty = true;
        Save();
    }

    public void PersistPending() => Save();

    private void Save()
    {
        if (_deadLettersDirty)
        {
            SaveEnvelope(_deadLetterPath, new DeadLetterState { Entries = _deadLetters });
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

    private static void SaveEnvelope<T>(string path, T state)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Collector Protocol state path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(new StateEnvelope<T>(1, state), JsonOptions),
            new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static void Validate(OutboxState state)
    {
        if (state.Facts is null || state.Gaps is null ||
            state.Facts.Any(item => item.MessageId == Guid.Empty || item.Fact.FactId == Guid.Empty ||
                                    item.Fact.Revision <= 0 || string.IsNullOrWhiteSpace(item.Fact.BindingId)) ||
            state.Gaps.Any(item => item.MessageId == Guid.Empty || item.Gap.GapId == Guid.Empty ||
                                   item.Gap.End < item.Gap.Start || string.IsNullOrWhiteSpace(item.Gap.BindingId)))
            throw new InvalidDataException("Collector Protocol outbox contains invalid state.");
    }

    private static (DateTimeOffset Start, DateTimeOffset End) FactRange(CollectorFact fact) => fact.Time switch
    {
        CollectorSegmentFactTime segment => (segment.Start, segment.End),
        CollectorEventFactTime occurrence => (occurrence.OccurredAt, occurrence.OccurredAt),
        _ => throw new InvalidOperationException("Unknown Collector Fact time shape.")
    };

    private sealed record StateEnvelope<T>(int SchemaVersion, T State);

    private sealed record OutboxState
    {
        public List<PendingCollectorFact> Facts { get; init; } = [];
        public List<PendingCollectorGap> Gaps { get; init; } = [];
    }

    private sealed record DeadLetterState
    {
        public List<CollectorDeadLetter> Entries { get; init; } = [];
    }
}

internal sealed record PendingCollectorFact(Guid MessageId, CollectorFact Fact);
internal sealed record PendingCollectorGap(Guid MessageId, CollectorStreamGap Gap);
