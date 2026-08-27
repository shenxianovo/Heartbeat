using Heartbeat.Collector.VRChat;

namespace Heartbeat.Collector.VRChat.Tests;

public sealed class VRChatOutboxTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-vrchat-outbox-{Guid.NewGuid():N}");

    [Fact]
    public void RestartKeepsLatestRevisionAndDisclosesTheUnobservedGap()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "outbox.json");
        var factId = Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999");
        var start = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        var outbox = VRChatOutbox.Open(path);
        outbox.Enqueue(Fact(factId, 1, start, start, isFinal: false));
        outbox.Enqueue(Fact(factId, 2, start, start.AddMinutes(1), isFinal: false));

        var reopened = VRChatOutbox.Open(path);
        reopened.RecoverInterruptedPresence(start.AddMinutes(3));

        var pending = Assert.Single(reopened.PendingFacts);
        Assert.Equal(factId, pending.FactId);
        Assert.Equal(3, pending.Revision);
        Assert.True(pending.IsFinal);
        Assert.Equal(start.AddMinutes(1), pending.End);
        var gap = Assert.Single(reopened.PendingGaps);
        Assert.Equal(start.AddMinutes(1), gap.Start);
        Assert.Equal(start.AddMinutes(3), gap.End);
        Assert.Equal("process_restart", gap.Reason);

        reopened.AcknowledgeFact(pending.FactId, pending.Revision);
        reopened.AcknowledgeGap(gap.GapId);
        var empty = VRChatOutbox.Open(path);
        Assert.Empty(empty.PendingFacts);
        Assert.Empty(empty.PendingGaps);
    }

    [Fact]
    public void CorruptState_IsQuarantinedAndDisclosedAsStreamGap()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "outbox.json");
        var lastGoodBoundary = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        var recoveredAt = lastGoodBoundary.AddMinutes(4);
        File.WriteAllText(path, "{truncated");
        File.SetLastWriteTimeUtc(path, lastGoodBoundary.UtcDateTime);

        var recovered = VRChatOutbox.Open(path, recoveredAt: recoveredAt);

        var gap = Assert.Single(recovered.PendingGaps);
        Assert.Equal("outbox_corrupted", gap.Reason);
        Assert.Equal(lastGoodBoundary, gap.Start);
        Assert.Equal(recoveredAt, gap.End);
        Assert.Single(Directory.EnumerateFiles(_directory, "outbox.json.corrupt-*"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void LegacyOutboxWithoutSchemaVersion_RemainsReadable()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "outbox.json");
        var start = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        var outbox = VRChatOutbox.Open(path);
        outbox.Enqueue(Fact(Guid.CreateVersion7(), 1, start, start, isFinal: false));
        var legacyJson = File.ReadAllText(path)
            .Replace("  \"schemaVersion\": 1,\n", string.Empty, StringComparison.Ordinal);
        File.WriteAllText(path, legacyJson);

        var reopened = VRChatOutbox.Open(path);

        Assert.Single(reopened.PendingFacts);
        Assert.Empty(reopened.PendingGaps);
    }

    private static VRChatPresenceFact Fact(
        Guid factId,
        long revision,
        DateTimeOffset start,
        DateTimeOffset end,
        bool isFinal) => new(
            factId,
            revision,
            start,
            end,
            isFinal,
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
