using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Collector.VRChat;

internal sealed record VRChatPresenceRecoveryGap(
    Guid GapId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Reason);

internal sealed class VRChatPresenceCheckpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _path;

    private VRChatPresenceCheckpoint(
        string path,
        VRChatPresenceFact? active,
        IReadOnlyList<VRChatPresenceFact> pendingFacts,
        IReadOnlyList<VRChatPresenceRecoveryGap> pendingGaps,
        VRChatPresenceRecoveryGap? recoveryGap)
    {
        _path = path;
        Active = active;
        PendingFacts = pendingFacts;
        PendingGaps = pendingGaps;
        RecoveryGap = recoveryGap;
    }

    public VRChatPresenceFact? Active { get; private set; }
    public IReadOnlyList<VRChatPresenceFact> PendingFacts { get; private set; }
    public IReadOnlyList<VRChatPresenceRecoveryGap> PendingGaps { get; private set; }
    public VRChatPresenceRecoveryGap? RecoveryGap { get; }

    public static VRChatPresenceCheckpoint Open(string path, DateTimeOffset recoveredAt)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return new VRChatPresenceCheckpoint(fullPath, null, [], [], null);
        try
        {
            var envelope = JsonSerializer.Deserialize<CheckpointEnvelope>(
                File.ReadAllText(fullPath, Encoding.UTF8),
                JsonOptions) ?? throw new JsonException("VRChat presence checkpoint is empty.");
            if (envelope.SchemaVersion is not (1 or 2) || envelope.Active is { IsFinal: true })
                throw new JsonException("VRChat presence checkpoint is invalid.");
            if (envelope.SchemaVersion == 1
                && ((envelope.PendingFacts?.Count ?? 0) != 0 || (envelope.PendingGaps?.Count ?? 0) != 0))
                throw new JsonException("VRChat presence v1 checkpoint contains v2 fields.");
            return new VRChatPresenceCheckpoint(
                fullPath,
                envelope.Active,
                envelope.PendingFacts ?? [],
                envelope.PendingGaps ?? [],
                null);
        }
        catch (JsonException)
        {
            var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero);
            File.Move(fullPath, fullPath + $".corrupt-{recoveredAt:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
            var recoveryGap = new VRChatPresenceRecoveryGap(
                Guid.CreateVersion7(),
                lastWrite <= recoveredAt ? lastWrite : recoveredAt,
                recoveredAt,
                "presence_checkpoint_corrupted");
            var recovered = new VRChatPresenceCheckpoint(
                fullPath,
                null,
                [],
                [recoveryGap],
                recoveryGap);
            recovered.Persist();
            return recovered;
        }
    }

    public void Stage(
        IReadOnlyList<VRChatPresenceFact> facts,
        IReadOnlyList<VRChatPresenceRecoveryGap>? gaps = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        gaps ??= [];
        if (PendingFacts.Count != 0 || PendingGaps.Count != 0)
            throw new InvalidOperationException("Pending VRChat presence outputs must be acknowledged before staging more.");
        if (facts.Count == 0 && gaps.Count == 0)
            return;

        var previousActive = Active;
        var nextActive = Active;
        foreach (var fact in facts)
        {
            if (!fact.IsFinal)
                nextActive = fact;
            else if (nextActive?.FactId == fact.FactId)
                nextActive = null;
        }
        Active = nextActive;
        PendingFacts = [.. facts];
        PendingGaps = [.. gaps];
        try
        {
            Persist();
        }
        catch
        {
            Active = previousActive;
            PendingFacts = [];
            PendingGaps = [];
            throw;
        }
    }

    public void Acknowledge(VRChatPresenceFact fact)
    {
        if (PendingFacts.Count == 0 || PendingFacts[0] != fact)
            throw new InvalidOperationException("VRChat presence facts must be acknowledged in staged order.");
        var previous = PendingFacts;
        PendingFacts = [.. PendingFacts.Skip(1)];
        try
        {
            Persist();
        }
        catch
        {
            PendingFacts = previous;
            throw;
        }
    }

    public void Acknowledge(VRChatPresenceRecoveryGap gap)
    {
        if (PendingGaps.Count == 0 || PendingGaps[0] != gap)
            throw new InvalidOperationException("VRChat presence gaps must be acknowledged in staged order.");
        var previous = PendingGaps;
        PendingGaps = [.. PendingGaps.Skip(1)];
        try
        {
            Persist();
        }
        catch
        {
            PendingGaps = previous;
            throw;
        }
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(
                new CheckpointEnvelope(2, Active, PendingFacts, PendingGaps),
                JsonOptions),
            new UTF8Encoding(false));
        try
        {
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private sealed record CheckpointEnvelope(
        int SchemaVersion,
        VRChatPresenceFact? Active,
        IReadOnlyList<VRChatPresenceFact>? PendingFacts = null,
        IReadOnlyList<VRChatPresenceRecoveryGap>? PendingGaps = null);
}
