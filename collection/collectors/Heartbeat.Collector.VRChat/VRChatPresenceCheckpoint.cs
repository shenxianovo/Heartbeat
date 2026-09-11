using Heartbeat.Core.DTOs.Facts;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        var parsed = false;
        try
        {
            var document = JsonNode.Parse(File.ReadAllText(fullPath, Encoding.UTF8));
            parsed = true;
            if (document is not JsonObject root ||
                root["SchemaVersion"] is not JsonValue versionValue || !versionValue.TryGetValue<int>(out var version))
                throw new JsonException("Invalid checkpoint envelope.");
            if (version is not (1 or 2 or 3 or 4))
                throw new InvalidDataException($"Unsupported VRChat checkpoint schema {version}.");
            if (version is 1 or 2)
            {
                var snapshots = new List<JsonNode?> { root["Active"] };
                if (root["PendingFacts"] is JsonArray pending) snapshots.AddRange(pending);
                foreach (var node in snapshots.OfType<JsonObject>())
                    if (node.Remove("IdentityKey", out var oldKey))
                    {
                        if (node["ActivityKey"] is { } currentKey && !JsonNode.DeepEquals(currentKey, oldKey))
                            throw new InvalidDataException("VRChat checkpoint contains conflicting activity identities.");
                        node["ActivityKey"] = oldKey;
                    }
            }
            var envelope = root.Deserialize<CheckpointEnvelope>(JsonOptions) ?? throw new JsonException("VRChat presence checkpoint is empty.");
            Validate(envelope);
            if (envelope.SchemaVersion == 1
                && ((envelope.PendingFacts?.Count ?? 0) != 0 || (envelope.PendingGaps?.Count ?? 0) != 0))
                throw new JsonException("VRChat presence v1 checkpoint contains v2 fields.");
            var checkpoint = new VRChatPresenceCheckpoint(
                fullPath,
                envelope.Active,
                envelope.PendingFacts ?? [],
                envelope.PendingGaps ?? [],
                null);
            if (version < 4)
            {
                var backup = fullPath + $".v{version}.bak";
                if (!File.Exists(backup)) File.Copy(fullPath, backup);
                checkpoint.Persist();
            }
            return checkpoint;
        }
        catch (JsonException exception) when (parsed)
        {
            throw new InvalidDataException("VRChat presence checkpoint could not be loaded; original state was preserved.", exception);
        }
        catch (JsonException)
        {
            var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero);
            // Keep the source in place until the recovery Gap is durably published. A failed
            // requirements/checkpoint write must let the next Open retry this recovery.
            File.Copy(fullPath, fullPath + $".corrupt-{recoveredAt:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
            var recoveryGap = lastWrite < recoveredAt
                ? new VRChatPresenceRecoveryGap(
                    Guid.CreateVersion7(),
                    lastWrite,
                    recoveredAt,
                    "presence_checkpoint_corrupted")
                : null;
            var recovered = new VRChatPresenceCheckpoint(
                fullPath,
                null,
                [],
                recoveryGap is null ? [] : [recoveryGap],
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
        var envelope = new CheckpointEnvelope(4, Active, PendingFacts, PendingGaps);
        Validate(envelope);
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        PersistRequirements(directory);
        WriteAtomically(_path, json);
    }

    private static void Validate(CheckpointEnvelope envelope)
    {
        if (envelope.SchemaVersion is not (1 or 2 or 3 or 4) || envelope.Active is { IsFinal: true })
            throw new InvalidDataException("VRChat presence checkpoint is invalid.");
        var snapshots = (envelope.PendingFacts ?? []).AsEnumerable();
        if (envelope.Active is { } active) snapshots = snapshots.Prepend(active);
        foreach (var fact in snapshots)
        {
            if (fact is null || fact.FactId == Guid.Empty || fact.Revision <= 0 || fact.End < fact.Start ||
                string.IsNullOrWhiteSpace(fact.ActivityKey) || string.IsNullOrWhiteSpace(fact.WorldId) ||
                string.IsNullOrWhiteSpace(fact.InstanceId))
                throw new InvalidDataException("VRChat presence checkpoint contains an invalid fact.");
            if (fact.ObservedAccountId is not null && !ServiceAccountReference.IsVRChatAccountId(fact.ObservedAccountId))
                throw new InvalidDataException("VRChat presence checkpoint contains an invalid account.");
            if (fact.IsNativeObservation && (envelope.SchemaVersion < 4 || fact.CollectorId is null ||
                    fact.CollectorId == Guid.Empty || !ServiceAccountReference.IsVRChatAccountId(fact.ObservedAccountId)))
                throw new InvalidDataException("Native VRChat presence requires schema 4 and persisted observer/account identities.");
            if (!fact.IsNativeObservation && fact.CollectorId is not null)
                throw new InvalidDataException("Legacy VRChat presence cannot acquire a native observer identity.");
        }
        foreach (var gap in envelope.PendingGaps ?? [])
            if (gap is null || gap.GapId == Guid.Empty || gap.Start >= gap.End || string.IsNullOrWhiteSpace(gap.Reason))
                throw new InvalidDataException("VRChat presence checkpoint contains an invalid gap.");
    }

    private static void PersistRequirements(string directory)
    {
        var path = Path.Combine(directory, "collector-data-requirements.json");
        Dictionary<string, int> requirements = [];
        if (File.Exists(path))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2)
                    throw new InvalidDataException("Invalid Collector data requirements envelope.");
                var envelope = root.Deserialize<DataRequirementsEnvelope>(JsonOptions);
                if (envelope is not { SchemaVersion: 1, RequiredCapabilities: { } existing } ||
                    existing.Count == 0 ||
                    root.GetProperty("RequiredCapabilities").EnumerateObject().Count() != existing.Count ||
                    existing.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0))
                    throw new InvalidDataException("Invalid Collector data requirements; original state was preserved.");
                requirements = existing;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Unreadable Collector data requirements; original state was preserved.", exception);
            }
        }
        if (requirements.TryGetValue("facts.observation", out var version) && version != 2)
            throw new InvalidDataException("Conflicting observation capability in Collector data requirements.");
        requirements["facts.observation"] = 2;
        WriteAtomically(path, JsonSerializer.Serialize(new DataRequirementsEnvelope(1, requirements), JsonOptions));
    }

    private static void WriteAtomically(string path, string json)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(json));
                stream.Flush(flushToDisk: true);
            }
            // Verify the complete written bytes before replacing the previous durable state.
            using var verified = JsonDocument.Parse(File.ReadAllText(temporary, Encoding.UTF8));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record DataRequirementsEnvelope(int SchemaVersion, Dictionary<string, int> RequiredCapabilities);

    private sealed record CheckpointEnvelope(
        int SchemaVersion,
        VRChatPresenceFact? Active,
        IReadOnlyList<VRChatPresenceFact>? PendingFacts = null,
        IReadOnlyList<VRChatPresenceRecoveryGap>? PendingGaps = null);
}
