using Heartbeat.Collector.VRChat;

namespace Heartbeat.Collector.VRChat.Tests;

public sealed class VRChatPresenceCheckpointTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-vrchat-presence-{Guid.NewGuid():N}");

    [Fact]
    public void RestartRestoresTheActiveDomainSnapshot()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "presence.json");
        var start = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        var active = Fact(start);
        var checkpoint = VRChatPresenceCheckpoint.Open(path, start);

        checkpoint.Save(active);
        var reopened = VRChatPresenceCheckpoint.Open(path, start.AddMinutes(3));

        Assert.Equal(active, reopened.Active);
        Assert.Null(reopened.RecoveryGap);
    }

    [Fact]
    public void CorruptCheckpointIsQuarantinedAndReturnsARecoveryGap()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "presence.json");
        var lastGoodBoundary = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        var recoveredAt = lastGoodBoundary.AddMinutes(4);
        File.WriteAllText(path, "{truncated");
        File.SetLastWriteTimeUtc(path, lastGoodBoundary.UtcDateTime);

        var recovered = VRChatPresenceCheckpoint.Open(path, recoveredAt);

        Assert.Null(recovered.Active);
        Assert.Equal("presence_checkpoint_corrupted", recovered.RecoveryGap?.Reason);
        Assert.Equal(lastGoodBoundary, recovered.RecoveryGap?.Start);
        Assert.Equal(recoveredAt, recovered.RecoveryGap?.End);
        Assert.Single(Directory.EnumerateFiles(_directory, "presence.json.corrupt-*"));
    }

    private static VRChatPresenceFact Fact(DateTimeOffset start) => new(
        Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999"),
        2,
        start,
        start.AddMinutes(1),
        false,
        "wrld_alpha|instance:one",
        "Alpha",
        "wrld_alpha",
        "Alpha",
        "instance:one");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
