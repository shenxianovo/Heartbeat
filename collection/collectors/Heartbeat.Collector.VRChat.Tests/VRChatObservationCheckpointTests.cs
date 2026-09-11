using Heartbeat.Collector.VRChat;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Heartbeat.Collector.VRChat.Tests;

public sealed class VRChatObservationCheckpointTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"heartbeat-vrchat-observation-checkpoint-{Guid.NewGuid():N}");
    private static readonly DateTimeOffset RecoveredAt = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnsupportedCheckpointVersionPreservesOriginalWithoutInventingRecoveryGap()
    {
        var path = Write("""{"SchemaVersion":99,"Active":null,"FutureState":{"Account":"unknown"}}""");
        var original = File.ReadAllText(path);

        Assert.Throws<InvalidDataException>(() => VRChatPresenceCheckpoint.Open(path, RecoveredAt));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Single(Directory.EnumerateFiles(_directory));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void HistoricalCheckpointUpgradesWithoutReassigningIdentityOrLosingPendingOutputs(int version, bool knownAccount)
    {
        var original = HistoricalCheckpoint(version, knownAccount);
        var path = Write(original);
        var checkpoint = VRChatPresenceCheckpoint.Open(path, RecoveredAt);

        var active = Assert.IsType<VRChatPresenceFact>(checkpoint.Active);
        Assert.Equal(Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999"), active.FactId);
        Assert.Equal(7, active.Revision);
        Assert.Equal("wrld_alpha|instance:one", active.ActivityKey);
        Assert.Equal(RecoveredAt.AddHours(-1), active.Start);
        Assert.Equal(RecoveredAt.AddMinutes(-50), active.End);
        Assert.Equal(knownAccount ? "usr_11111111-1111-4111-8111-111111111111" : null, active.ObservedAccountId);
        Assert.Null(checkpoint.RecoveryGap);
        Assert.False(active.IsNativeObservation);
        Assert.Null(active.CollectorId);
        if (version == 1)
        {
            Assert.Empty(checkpoint.PendingFacts);
            Assert.Empty(checkpoint.PendingGaps);
        }
        else
        {
            Assert.Equal(active, Assert.Single(checkpoint.PendingFacts));
            var gap = Assert.Single(checkpoint.PendingGaps);
            Assert.Equal(Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8998"), gap.GapId);
            Assert.Equal("process_restart", gap.Reason);
            Assert.Equal(RecoveredAt.AddHours(-2), gap.Start);
            Assert.Equal(RecoveredAt.AddHours(-1), gap.End);
        }
        using var upgraded = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(4, upgraded.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(original, File.ReadAllText(path + $".v{version}.bak"));
        var restarted = VRChatPresenceCheckpoint.Open(path, RecoveredAt.AddDays(1));
        Assert.Equal(active, restarted.Active);
        Assert.Equal(checkpoint.PendingFacts, restarted.PendingFacts);
        Assert.Equal(checkpoint.PendingGaps, restarted.PendingGaps);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"SchemaVersion\":2,\"SchemaVersion\":1,\"RequiredCapabilities\":{\"facts.observation\":2}}")]
    [InlineData("{\"SchemaVersion\":1,\"RequiredCapabilities\":{\"facts.observation\":3,\"facts.observation\":2}} ")]
    [InlineData("{\"SchemaVersion\":1,\"RequiredCapabilities\":{}}")]
    [InlineData("{\"SchemaVersion\":2,\"RequiredCapabilities\":{\"facts.observation\":2}}")]
    [InlineData("{\"SchemaVersion\":1,\"RequiredCapabilities\":{\"facts.observation\":1}}")]
    [InlineData("{\"SchemaVersion\":1,\"RequiredCapabilities\":{\"future.feature\":0}}")]
    public void IncompatibleRequirementsMarkerPreventsCheckpointUpgradeAndCanBeRetried(string marker)
    {
        var original = HistoricalCheckpoint(3, true);
        var path = Write(original);
        var requirementsPath = Path.Combine(_directory, "collector-data-requirements.json");
        File.WriteAllText(requirementsPath, marker);

        Assert.Throws<InvalidDataException>(() => VRChatPresenceCheckpoint.Open(path, RecoveredAt));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(marker, File.ReadAllText(requirementsPath));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.corrupt-*"));
        File.Delete(requirementsPath);
        var retry = VRChatPresenceCheckpoint.Open(path, RecoveredAt);
        Assert.Equal(7, retry.Active!.Revision);
        Assert.Single(retry.PendingFacts);
        Assert.Single(retry.PendingGaps);
    }

    [Theory]
    [InlineData(3, true, "c0c4d7c1-d38f-4f92-a44c-f96065793e58", true)]
    [InlineData(4, true, null, true)]
    [InlineData(4, true, "00000000-0000-0000-0000-000000000000", true)]
    [InlineData(4, true, "c0c4d7c1-d38f-4f92-a44c-f96065793e58", false)]
    public void InvalidNativeIdentityIsRejectedBeforeChangingCheckpoint(int version, bool native, string? observer, bool knownAccount)
    {
        var root = JsonNode.Parse(HistoricalCheckpoint(3, knownAccount))!.AsObject();
        root["SchemaVersion"] = version;
        root["Active"]!["IsNativeObservation"] = native;
        root["Active"]!["CollectorId"] = observer;
        var path = Write(root.ToJsonString());
        var original = File.ReadAllText(path);

        Assert.Throws<InvalidDataException>(() => VRChatPresenceCheckpoint.Open(path, RecoveredAt));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Single(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public void NativeCheckpointAndRequirementRemainAfterExactAcknowledgementAndDrain()
    {
        var path = Write(HistoricalCheckpoint(1));
        var checkpoint = VRChatPresenceCheckpoint.Open(path, RecoveredAt);
        var previous = checkpoint.Active!;
        var final = previous with { IsFinal = true, Revision = previous.Revision + 1 };
        checkpoint.Stage([final]);
        checkpoint.Acknowledge(final);
        var marker = Path.Combine(_directory, "collector-data-requirements.json");
        File.WriteAllText(marker, """{"SchemaVersion":1,"RequiredCapabilities":{"other.capability":3,"facts.observation":2}}""");
        var machine = new PresenceStateMachine(collectorId: Guid.Parse("c0c4d7c1-d38f-4f92-a44c-f96065793e58"));
        var opened = Assert.Single(machine.Observe(new VRChatPresence("wrld_new", "New", "instance:two", "usr_22222222-2222-4222-8222-222222222222"), RecoveredAt));
        checkpoint.Stage([opened]);
        var reopened = VRChatPresenceCheckpoint.Open(path, RecoveredAt.AddDays(1));
        Assert.Equal(opened, reopened.Active);
        Assert.True(reopened.Active!.IsNativeObservation);
        Assert.Equal(Guid.Parse("c0c4d7c1-d38f-4f92-a44c-f96065793e58"), reopened.Active.CollectorId);
        Assert.Throws<InvalidOperationException>(() => reopened.Acknowledge(opened with { IsFinal = true }));
        reopened.Acknowledge(opened);
        var nativeFinal = Assert.Single(machine.Stop(RecoveredAt));
        reopened.Stage([nativeFinal]);
        reopened.Acknowledge(nativeFinal);
        var drained = VRChatPresenceCheckpoint.Open(path, RecoveredAt.AddDays(2));
        Assert.Null(drained.Active);
        Assert.Empty(drained.PendingFacts);
        using var persisted = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(4, persisted.RootElement.GetProperty("SchemaVersion").GetInt32());
        using var requirements = JsonDocument.Parse(File.ReadAllText(marker));
        Assert.Equal(2, requirements.RootElement.GetProperty("RequiredCapabilities").GetProperty("facts.observation").GetInt32());
        Assert.Equal(3, requirements.RootElement.GetProperty("RequiredCapabilities").GetProperty("other.capability").GetInt32());
    }

    [Fact]
    public void FailedRequirementsPublicationPreservesHistoricalFileAndRetryKeepsIdentities()
    {
        var original = HistoricalCheckpoint(2);
        var path = Write(original);
        var marker = Path.Combine(_directory, "collector-data-requirements.json");
        Directory.CreateDirectory(marker);
        Assert.ThrowsAny<IOException>(() => VRChatPresenceCheckpoint.Open(path, RecoveredAt));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(original, File.ReadAllText(path + ".v2.bak"));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
        Directory.Delete(marker);
        var retry = VRChatPresenceCheckpoint.Open(path, RecoveredAt);
        Assert.Equal(Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999"), retry.Active!.FactId);
        Assert.Equal(7, retry.Active.Revision);
        Assert.Equal(retry.Active, Assert.Single(retry.PendingFacts));
        Assert.Single(retry.PendingGaps);
    }

    [Fact]
    public void CorruptCheckpointRecoveryRetriesItsGapWhenRequirementsPublicationFails()
    {
        var path = Write("{broken");
        var lastWrite = RecoveredAt.AddHours(-1);
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        var marker = Path.Combine(_directory, "collector-data-requirements.json");
        Directory.CreateDirectory(marker);

        Assert.ThrowsAny<IOException>(() => VRChatPresenceCheckpoint.Open(path, RecoveredAt));
        Assert.True(File.Exists(path));
        Assert.Equal("{broken", File.ReadAllText(path));
        Assert.Equal("{broken", File.ReadAllText(Assert.Single(Directory.EnumerateFiles(_directory, "*.corrupt-*"))));
        Directory.Delete(marker);
        var retry = VRChatPresenceCheckpoint.Open(path, RecoveredAt);
        var gap = Assert.Single(retry.PendingGaps);
        Assert.Equal("presence_checkpoint_corrupted", gap.Reason);
        Assert.Equal(lastWrite, gap.Start);
        Assert.Equal(RecoveredAt, gap.End);
        Assert.Equal(gap, Assert.Single(VRChatPresenceCheckpoint.Open(path, RecoveredAt).PendingGaps));
    }

    // These literal shapes come from cfa3e25 (v1), fea5bfe (v2), and 996264b (v3),
    // rather than serializing the current DTO and relabeling its schema.
    private static string HistoricalCheckpoint(int version, bool knownAccount = false)
    {
        var keyName = version < 3 ? "IdentityKey" : "ActivityKey";
        var account = version == 3 ? ",\"ObservedAccountId\":" + (knownAccount ? "\"usr_11111111-1111-4111-8111-111111111111\"" : "null") : "";
        var active = $$"""
            {"FactId":"0198d5eb-fc31-7d7b-8bf0-c2d009ec8999","Revision":7,
             "Start":"2026-09-11T09:00:00+00:00","End":"2026-09-11T09:10:00+00:00","IsFinal":false,
             "{{keyName}}":"wrld_alpha|instance:one","Title":"Alpha","WorldId":"wrld_alpha",
             "WorldName":"Alpha","InstanceId":"instance:one"{{account}}}
            """;
        return version == 1 ? $$"""{"SchemaVersion":1,"Active":{{active}}}""" : $$"""
            {"SchemaVersion":{{version}},"Active":{{active}},"PendingFacts":[{{active}}],
             "PendingGaps":[{"GapId":"0198d5eb-fc31-7d7b-8bf0-c2d009ec8998",
             "Start":"2026-09-11T08:00:00+00:00","End":"2026-09-11T09:00:00+00:00","Reason":"process_restart"}]}
            """;
    }

    private string Write(string json)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "presence.json");
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
