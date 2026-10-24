using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Observers.ForegroundState;
using Microsoft.Data.Sqlite;

namespace Heartbeat.Integration.Tests;

public sealed class ForegroundOutboxTests
{
    [Test]
    [Property("caseId", "foreground-outbox-recovery")]
    public async Task ActivePeriodStaysLocalAndRestartClosesLastCheckpointExactlyOnce()
    {
        var observer = EntityId.New();
        using var local = new ForegroundTestStore(observer);
        var capture = Capture(observer).Through(ForegroundTimelineTests.Reading(1).ObservedAt);
        local.Store.Checkpoint(capture, null);
        await Assert.That(local.Store.Pending().Count).IsEqualTo(0);
        var restarted = new ForegroundStore(local.Path, observer);
        restarted.FinishActive();
        restarted.FinishActive();
        var pending = restarted.Pending().Single();
        await Assert.That(pending.ObservationId).IsEqualTo(capture.ObservationId.Value);
        await Assert.That(JsonSerializer.Serialize(pending.Capture, JsonSerializerOptions.Web))
            .IsEqualTo(JsonSerializer.Serialize(capture, JsonSerializerOptions.Web));
        await Assert.That(() => new ForegroundStore(local.Path, EntityId.New())).Throws<InvalidDataException>();
        await Assert.That(restarted.Pending().Count).IsEqualTo(1);
    }

    [Test]
    [Property("caseId", "foreground-outbox-recovery")]
    public async Task FailedTransitionRollsBackBothOldPeriodAndNewCheckpoint()
    {
        var observer = EntityId.New();
        using var local = new ForegroundTestStore(observer);
        var first = Capture(observer);
        var second = Capture(observer);
        local.Store.Checkpoint(first, null);
        // Fail the second write, after the completed period was inserted in the same transaction.
        using (var connection = new SqliteConnection($"Data Source={local.Path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_checkpoint BEFORE UPDATE ON active_period BEGIN SELECT RAISE(ABORT, 'test failure'); END;";
            command.ExecuteNonQuery();
        }
        await Assert.That(() => local.Store.Checkpoint(second, first)).Throws<SqliteException>();
        await Assert.That(local.Store.Pending().Count).IsEqualTo(0);
        local.Store.FinishActive();
        await Assert.That(local.Store.Pending().Single().ObservationId).IsEqualTo(first.ObservationId.Value);
    }

    [Test]
    [Property("caseId", "foreground-application-period-boundaries")]
    public async Task EventsBetweenPollsArePreservedAndSleepClosesWithoutExtending()
    {
        var observer = EntityId.New();
        using var local = new ForegroundTestStore(observer);
        var collector = new ForegroundCollector(observer, new LocalApplicationRegistry(local.Path + ".apps"),
            local.Store, TimeSpan.FromSeconds(1), () => ForegroundTimelineTests.Reading(2), TextWriter.Null, TextWriter.Null);
        collector.Observe(ForegroundTimelineTests.Reading(0));
        collector.Observe(ForegroundTimelineTests.Reading(0) with { Name = "B", ObservedAt = ForegroundTimelineTests.Reading(0).ObservedAt.AddMilliseconds(100), Elapsed = TimeSpan.FromMilliseconds(100), Awake = TimeSpan.FromMilliseconds(100) });
        collector.Observe(ForegroundTimelineTests.Reading(0) with { Name = "C", ObservedAt = ForegroundTimelineTests.Reading(0).ObservedAt.AddMilliseconds(200), Elapsed = TimeSpan.FromMilliseconds(200), Awake = TimeSpan.FromMilliseconds(200) });
        await Assert.That(local.Store.Pending().Count).IsEqualTo(2);
        collector.Sleep();
        collector.Poll();
        await Assert.That(local.Store.Pending().Count).IsEqualTo(3);
        collector.Wake();
        await Assert.That(local.Store.Pending().Count).IsEqualTo(3);
        collector.Stop();
        await Assert.That(local.Store.Pending().Count).IsEqualTo(4);
        var end = local.Store.Pending()[2].Capture.EndAt;
        await Assert.That(end).IsEqualTo(ForegroundTimelineTests.Reading(0).ObservedAt.AddMilliseconds(200));
    }

    private static ForegroundCapture Capture(EntityId observer) => ForegroundCapture.Create(observer,
        new LocalApplication(EntityId.New(), []), ForegroundTimelineTests.Reading(0));

}
