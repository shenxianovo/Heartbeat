using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Collector.VRChat;

public sealed record VRChatStreamGap(
    Guid GapId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Reason,
    int? EstimatedFactsLost = null);

public sealed class VRChatOutbox
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly int _maxFacts;
    private OutboxState _state;

    private VRChatOutbox(string path, int maxFacts, OutboxState state)
    {
        _path = path;
        _maxFacts = maxFacts;
        _state = state;
    }

    public IReadOnlyList<VRChatPresenceFact> PendingFacts => _state.Facts;
    public IReadOnlyList<VRChatStreamGap> PendingGaps => _state.Gaps;

    public static VRChatOutbox Open(
        string path,
        int maxFacts = 512,
        DateTimeOffset? recoveredAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxFacts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFacts));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return new VRChatOutbox(fullPath, maxFacts, new OutboxState());
        try
        {
            var state = JsonSerializer.Deserialize<OutboxState>(
                File.ReadAllText(fullPath),
                SerializerOptions) ?? throw new JsonException("VRChat outbox is empty.");
            Validate(state);
            return new VRChatOutbox(fullPath, maxFacts, state);
        }
        catch (JsonException)
        {
            var now = recoveredAt ?? DateTimeOffset.UtcNow;
            var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero);
            var quarantine = fullPath + $".corrupt-{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            File.Move(fullPath, quarantine);
            var state = new OutboxState
            {
                Gaps =
                [
                    new VRChatStreamGap(
                        Guid.CreateVersion7(),
                        lastWrite <= now ? lastWrite : now,
                        now,
                        "outbox_corrupted")
                ]
            };
            var recovered = new VRChatOutbox(fullPath, maxFacts, state);
            recovered.Save();
            return recovered;
        }
    }

    public void Enqueue(VRChatPresenceFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var facts = _state.Facts
            .Where(existing => existing.FactId != fact.FactId)
            .Append(fact)
            .OrderBy(existing => existing.Start)
            .ThenBy(existing => existing.FactId)
            .ToList();
        var gaps = new List<VRChatStreamGap>(_state.Gaps);
        while (facts.Count > _maxFacts)
        {
            var evicted = facts[0];
            facts.RemoveAt(0);
            gaps.Add(new VRChatStreamGap(
                Guid.CreateVersion7(),
                evicted.Start,
                evicted.End,
                "outbox_capacity_exceeded",
                1));
        }
        _state = new OutboxState
        {
            Active = fact.IsFinal
                ? _state.Active?.FactId == fact.FactId ? null : _state.Active
                : fact,
            Facts = facts,
            Gaps = gaps
        };
        Save();
    }

    public void RecoverInterruptedPresence(DateTimeOffset recoveredAt)
    {
        if (_state.Active is not { } active)
            return;
        var final = active with
        {
            Revision = checked(active.Revision + 1),
            IsFinal = true
        };
        Enqueue(final);
        if (recoveredAt > active.End)
        {
            _state.Gaps.Add(new VRChatStreamGap(
                Guid.CreateVersion7(),
                active.End,
                recoveredAt,
                "process_restart"));
            Save();
        }
    }

    public void AcknowledgeFact(Guid factId, long revision)
    {
        _state.Facts.RemoveAll(fact => fact.FactId == factId && fact.Revision <= revision);
        Save();
    }

    public void AcknowledgeGap(Guid gapId)
    {
        _state.Gaps.RemoveAll(gap => gap.GapId == gapId);
        Save();
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(_state, SerializerOptions),
            new UTF8Encoding(false));
        File.Move(temporary, _path, overwrite: true);
    }

    private static void Validate(OutboxState state)
    {
        if (state.Facts is null || state.Gaps is null ||
            state.Facts.Any(fact => fact is null || fact.FactId == Guid.Empty || fact.Revision <= 0) ||
            state.Gaps.Any(gap => gap is null || gap.GapId == Guid.Empty || gap.End < gap.Start))
            throw new JsonException("VRChat outbox contains invalid state.");
        if (state.Facts.GroupBy(fact => fact.FactId).Any(group => group.Count() != 1))
            throw new JsonException("VRChat outbox contains duplicate Fact IDs.");
    }

    private sealed class OutboxState
    {
        public VRChatPresenceFact? Active { get; init; }
        public List<VRChatPresenceFact> Facts { get; init; } = [];
        public List<VRChatStreamGap> Gaps { get; init; } = [];
    }
}
