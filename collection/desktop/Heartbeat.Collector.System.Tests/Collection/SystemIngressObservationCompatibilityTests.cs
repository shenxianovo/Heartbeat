using System.Text.Json;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Core.DTOs.Input;

namespace Heartbeat.Collector.System.Tests.Collection;

public sealed class SystemIngressObservationCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-system-observation-compat-{Guid.NewGuid():N}");
    private string PathName => Path.Combine(_root, "system-collector-ingress.json");

    [Fact]
    public void InputFirstStageKeepsHistoricalIdentityAndMarksNewEventsAcrossRestart()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(PathName, """
            {"EntryId":"0197ea40-1111-7000-8000-000000000001","Kind":"input_event","InputEvent":{"Id":"0197ea40-2222-7000-8000-000000000001","EventType":1,"CodeSet":"windows-vk-v1","Code":65,"Timestamp":"2026-08-01T10:00:00.1234567+00:00"}}
            """ + "\n");
        var store = SystemCollectorIngressStore.Open(PathName, 2);
        var current = new InputEventItem
        {
            Id = Guid.Parse("0197ea40-3333-7000-8000-000000000001"),
            EventType = InputEventType.KeyDown,
            CodeSet = "heartbeat-key-position-v1",
            Code = 4,
            Timestamp = DateTimeOffset.Parse("2026-08-01T10:00:00.9876543+00:00")
        };
        store.StageInputEvent(current);

        var deliveries = SystemCollectorIngressStore.Open(PathName, 2).PeekInputDeliveries(10);
        Assert.Equal(2, deliveries.Count);
        Assert.Equal(Guid.Parse("0197ea40-2222-7000-8000-000000000001"), deliveries[0].Item!.Id);
        Assert.Equal("windows-vk-v1", deliveries[0].Item!.CodeSet);
        Assert.Equal(65, deliveries[0].Item!.Code);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T10:00:00.1234567+00:00"), deliveries[0].Item!.Timestamp);
        Assert.False(deliveries[0].IsObservation);
        Assert.True(deliveries[1].IsObservation);
        Assert.Equal(JsonSerializer.Serialize(current), JsonSerializer.Serialize(deliveries[1].Item));
    }

    [Fact]
    public void DrainingAndReclaimingChunksKeepsVersionFenceForOldPackages()
    {
        var store = SystemCollectorIngressStore.Open(PathName, 400);
        for (var index = 0; index < 400; index++)
            store.StageInputEvent(new InputEventItem
            {
                Id = Guid.CreateVersion7(), EventType = InputEventType.KeyDown,
                CodeSet = "heartbeat-key-position-v1", Code = 4,
                Timestamp = DateTimeOffset.UnixEpoch.AddTicks(index)
            });
        store.AcknowledgeInputDeliveries(store.PeekInputDeliveries(500));
        Assert.Empty(SystemCollectorIngressStore.Open(PathName, 400).PeekInputDeliveries(500));
        var chunk = Assert.Single(Directory.GetFiles(_root, "system-collector-ingress.json*"));
        using var reset = JsonDocument.Parse(File.ReadLines(chunk).Last());
        Assert.Equal("reset", reset.RootElement.GetProperty("Kind").GetString());
        Assert.Equal(2, reset.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    [Fact]
    public void LegacyAcknowledgedCheckpointFinalizesAtObservedBoundaryAndRecoveryFailureIsRetryable()
    {
        Directory.CreateDirectory(_root);
        const string legacy = """
            {"EntryId":"0197ea40-1111-7000-8000-000000000001","Kind":"reset","MutatesCheckpoint":true,"Checkpoint":{"FactId":"0197ea40-2222-7000-8000-000000000001","Revision":9,"IdentityKey":"system|win:code|draft","AppIdentityKey":"win:code","AppDisplayName":"Code","Title":"draft","Start":"2026-08-01T10:00:00+00:00","End":"2026-08-01T10:02:00+00:00","IsFinal":false}}
            """;
        File.WriteAllText(PathName, legacy + "\n");
        var original = File.ReadAllBytes(PathName);
        var recoveredAt = DateTimeOffset.Parse("2026-08-02T10:00:00+00:00");
        var failing = SystemCollectorIngressStore.Open(PathName, 2,
            new SystemCollectorIngressCommitFence(), () => throw new IOException("fixture publication failure"));
        Assert.Throws<IOException>(() => failing.RecoverInterruptedSegment(recoveredAt));
        Assert.Equal(original, File.ReadAllBytes(PathName));
        Assert.Empty(failing.PeekSegmentBatches(10));
        Assert.Equal(9, failing.ActiveSegmentCheckpoint!.Revision);

        var retry = SystemCollectorIngressStore.Open(PathName, 2);
        retry.RecoverInterruptedSegment(recoveredAt);
        var recovered = SystemCollectorIngressStore.Open(PathName, 2);
        recovered.RecoverInterruptedSegment(recoveredAt.AddMinutes(1));
        var final = Assert.Single(Assert.Single(recovered.PeekSegmentBatches(10)).Snapshots);
        Assert.Equal(Guid.Parse("0197ea40-2222-7000-8000-000000000001"), final.FactId);
        Assert.Equal(10, final.Revision);
        Assert.True(final.IsFinal);
        Assert.False(final.IsObservation);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T10:00:00+00:00"), final.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T10:02:00+00:00"), final.End);
        var gap = Assert.Single(recovered.PeekSegmentGaps(10)).Gap;
        Assert.Equal(final.End, gap.Start);
        Assert.Equal(recoveredAt, gap.End);
        Assert.Null(recovered.ActiveSegmentCheckpoint);
    }

    [Fact]
    public void UnsupportedSchemaPreservesJournalForCompatibleRetryWithoutGap()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(PathName, """
            {"EntryId":"0197ea40-1111-7000-8000-000000000001","Kind":"reset","SchemaVersion":3}
            """ + "\n");
        var original = File.ReadAllBytes(PathName);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Throws<NotSupportedException>(() => SystemCollectorIngressStore.Open(PathName, 2));
            Assert.Equal(original, File.ReadAllBytes(PathName));
            Assert.Single(Directory.GetFiles(_root));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
