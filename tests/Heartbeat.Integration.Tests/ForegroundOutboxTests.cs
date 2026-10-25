using Heartbeat.Observer.Sdk;
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
        await Assert.That(local.Pending().Count).IsEqualTo(0);
        using var http = new HttpClient();
        using var restartedSdk = new ObserverClient(local.Path, observer, http);
        var restarted = new ForegroundStore(restartedSdk, observer, local.Registration);
        restarted.FinishActive();
        restarted.FinishActive();
        var pending = local.Pending().Single();
        await Assert.That(pending.Version.EntityId.Value).IsEqualTo(capture.ObservationId.Value);
        var observation = (Observation)pending.Version.Content;
        await Assert.That(observation.EndAt).IsEqualTo(capture.EndAt);
        await Assert.That(observation.StartAt).IsEqualTo(capture.StartAt);
        await Assert.That(() => new ObserverClient(local.Path, EntityId.New(), http)).Throws<InvalidDataException>();
        await Assert.That(local.Pending().Count).IsEqualTo(1);
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
            command.CommandText = "CREATE TRIGGER reject_checkpoint BEFORE INSERT ON local_versions WHEN json_extract(NEW.payload, '$.category') = 'observation' BEGIN SELECT RAISE(ABORT, 'test failure'); END;";
            command.ExecuteNonQuery();
        }
        await Assert.That(() => local.Store.Checkpoint(second, first)).Throws<SqliteException>();
        await Assert.That(local.Pending().Count).IsEqualTo(0);
        using (var connection = new SqliteConnection($"Data Source={local.Path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER reject_checkpoint";
            command.ExecuteNonQuery();
        }
        local.Store.FinishActive();
        await Assert.That(local.Pending().Single().Version.EntityId.Value).IsEqualTo(first.ObservationId.Value);
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
        await Assert.That(local.Pending().Count).IsEqualTo(2);
        collector.Sleep();
        collector.Poll();
        await Assert.That(local.Pending().Count).IsEqualTo(3);
        collector.Wake();
        await Assert.That(local.Pending().Count).IsEqualTo(3);
        collector.Stop();
        await Assert.That(local.Pending().Count).IsEqualTo(4);
        var end = ((Observation)local.Pending()[2].Version.Content).EndAt;
        await Assert.That(end).IsEqualTo(ForegroundTimelineTests.Reading(0).ObservedAt.AddMilliseconds(200));
    }

    private static ForegroundCapture Capture(EntityId observer) => ForegroundCapture.Create(observer,
        new LocalApplication(EntityId.New(), []), ForegroundTimelineTests.Reading(0));

}
