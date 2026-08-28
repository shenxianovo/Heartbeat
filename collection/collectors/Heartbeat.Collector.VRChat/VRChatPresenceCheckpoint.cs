using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Collector.VRChat;

internal sealed record VRChatPresenceRecoveryGap(
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
        VRChatPresenceRecoveryGap? recoveryGap)
    {
        _path = path;
        Active = active;
        RecoveryGap = recoveryGap;
    }

    public VRChatPresenceFact? Active { get; private set; }
    public VRChatPresenceRecoveryGap? RecoveryGap { get; }

    public static VRChatPresenceCheckpoint Open(string path, DateTimeOffset recoveredAt)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return new VRChatPresenceCheckpoint(fullPath, null, null);
        try
        {
            var envelope = JsonSerializer.Deserialize<CheckpointEnvelope>(
                File.ReadAllText(fullPath, Encoding.UTF8),
                JsonOptions) ?? throw new JsonException("VRChat presence checkpoint is empty.");
            if (envelope.SchemaVersion != 1 || envelope.Active is { IsFinal: true })
                throw new JsonException("VRChat presence checkpoint is invalid.");
            return new VRChatPresenceCheckpoint(fullPath, envelope.Active, null);
        }
        catch (JsonException)
        {
            var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero);
            File.Move(fullPath, fullPath + $".corrupt-{recoveredAt:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
            return new VRChatPresenceCheckpoint(
                fullPath,
                null,
                new VRChatPresenceRecoveryGap(
                    lastWrite <= recoveredAt ? lastWrite : recoveredAt,
                    recoveredAt,
                    "presence_checkpoint_corrupted"));
        }
    }

    public void Save(VRChatPresenceFact? active)
    {
        if (active is { IsFinal: true })
            throw new ArgumentException("A final VRChat presence is not an active checkpoint.", nameof(active));
        Active = active;
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(new CheckpointEnvelope(1, active), JsonOptions),
            new UTF8Encoding(false));
        File.Move(temporary, _path, overwrite: true);
    }

    private sealed record CheckpointEnvelope(int SchemaVersion, VRChatPresenceFact? Active);
}
