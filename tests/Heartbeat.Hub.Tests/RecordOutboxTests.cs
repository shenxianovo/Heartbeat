using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Heartbeat.Hub.Tests;

public sealed class RecordOutboxTests : IDisposable
{
    private readonly QueueFixture _fixture = new();

    [Fact]
    public void OversizedNormalizedBatchIsNotAcknowledged()
    {
        var queue = _fixture.Open();
        var record = QueueFixture.Snapshot() with
        {
            Value = JsonSerializer.SerializeToElement(new { text = new string('a', RecordOutbox.MaximumBatchBytes) }),
        };
        Assert.Throws<ArgumentException>(() => queue.Accept(QueueFixture.Submission(record)));
        Assert.Equal(new QueueStatus(0, 0), queue.Status());
    }

    [Fact]
    public void AcceptedSnapshotsSurviveReopenAndOutOfOrderRetries()
    {
        var queue = _fixture.Open();
        var record = QueueFixture.Snapshot(minutes: 3);
        queue.Accept(QueueFixture.Submission(record, record with { EndedAt = record.StartedAt }));
        var reopened = _fixture.Open();
        var restored = Assert.Single(reopened.TakePending());
        Assert.Equal(record.Id, restored.Record.Id);
        Assert.Equal(record.EndedAt, restored.Record.EndedAt);
        Assert.True(JsonElement.DeepEquals(record.Value, restored.Record.Value));
    }

    [Fact]
    public void LateReceiptCannotDeleteOrSuspendProgressAcceptedThroughAnotherConnection()
    {
        var queue = _fixture.Open();
        var record = QueueFixture.Snapshot();
        queue.Accept(QueueFixture.Submission(record));
        var sent = Assert.Single(queue.TakePending());
        var newer = record with { EndedAt = record.EndedAt!.Value.AddMinutes(1) };
        _fixture.Open().Accept(QueueFixture.Submission(newer));
        queue.Apply([new(sent, true), new(sent, false, "invalid_record")]);

        var pending = Assert.Single(queue.TakePending());
        Assert.Equal(newer.EndedAt, pending.Record.EndedAt);
        Assert.Null(pending.Failure);
        queue.Apply([new(pending, true)]);
        Assert.Equal(new QueueStatus(0, 0), queue.Status());
    }

    [Fact]
    public void ConflictRollsBackTheWholeLocalBatch()
    {
        var queue = _fixture.Open();
        var existing = QueueFixture.Snapshot();
        queue.Accept(QueueFixture.Submission(existing));
        var conflict = existing with { Value = JsonSerializer.SerializeToElement(new { different = true }) };
        Assert.Throws<RecordConflictException>(() => queue.Accept(QueueFixture.Submission(QueueFixture.Snapshot(), conflict)));
        Assert.Equal(new QueueStatus(1, 0), _fixture.Open().Status());
        Assert.Throws<RecordConflictException>(() => queue.Accept(new HubSubmission(
            QueueFixture.Collector(), new TrackDeclaration("other.data", 1, "range", "explicit"), [existing])));
    }

    [Fact]
    public void EquivalentJsonAndTimeOffsetsAreTheSameFixedContent()
    {
        var queue = _fixture.Open();
        var record = QueueFixture.Snapshot() with { Value = JsonSerializer.SerializeToElement(new { a = 1, b = 2 }) };
        queue.Accept(QueueFixture.Submission(record));
        queue.Accept(QueueFixture.Submission(record with
        {
            Value = JsonSerializer.SerializeToElement(new { b = 2, a = 1 }),
            StartedAt = record.StartedAt!.Value.ToOffset(TimeSpan.FromHours(8)),
            ObservedAt = record.StartedAt,
        }));
        Assert.Equal(1, queue.Status().Pending);
    }

    [Fact]
    public async Task ConcurrentExtensionsConvergeAcrossQueueConnections()
    {
        var first = _fixture.Open();
        var second = _fixture.Open();
        var record = QueueFixture.Snapshot();
        var writes = Enumerable.Range(1, 16).Select(index => Task.Run(() =>
            (index % 2 == 0 ? first : second).Accept(QueueFixture.Submission(
                record with { EndedAt = record.StartedAt!.Value.AddMinutes(index) }))));
        await Task.WhenAll(writes);
        Assert.Equal(record.StartedAt!.Value.AddMinutes(16), Assert.Single(first.TakePending()).Record.EndedAt);
    }

    [Fact]
    public void CapacityRejectsNewRecordsButAllowsExtensionsAndDoesNotEraseFailures()
    {
        var queue = _fixture.Open(1);
        var record = QueueFixture.Snapshot();
        queue.Accept(QueueFixture.Submission(record));
        queue.Apply([new(Assert.Single(queue.TakePending()), false, "conflict")]);
        queue.Accept(QueueFixture.Submission(record with { EndedAt = record.EndedAt!.Value.AddMinutes(1) }));
        Assert.Throws<QueueCapacityException>(() => queue.Accept(QueueFixture.Submission(QueueFixture.Snapshot())));
        Assert.Equal(new QueueStatus(0, 1), _fixture.Open().Status());
        Assert.Equal("conflict", Assert.Single(queue.ReadFailures()).Failure);
        Assert.Empty(queue.TakePending());
    }

    [Fact]
    public void DatabaseFailureCannotReturnAcceptanceOrLeavePartialBatch()
    {
        var queue = _fixture.Open();
        using var connection = new SqliteConnection($"Data Source={_fixture.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER fail_second BEFORE INSERT ON records WHEN (SELECT COUNT(*) FROM records) > 0
            BEGIN SELECT RAISE(ABORT, 'simulated storage failure'); END;
            """;
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => queue.Accept(QueueFixture.Submission(QueueFixture.Snapshot(), QueueFixture.Snapshot())));
        Assert.Equal(new QueueStatus(0, 0), queue.Status());
    }

    [Fact]
    public void DatabaseBindingCannotChangeEvenAfterTheQueueEmpties()
    {
        _fixture.Open();
        Assert.Throws<InvalidOperationException>(() => new RecordOutbox(_fixture.DatabasePath,
            new DeliveryDestination(_fixture.Destination.BackendUrl, Guid.NewGuid())));
        Assert.Throws<InvalidOperationException>(() => new RecordOutbox(_fixture.DatabasePath,
            new DeliveryDestination(new Uri("http://localhost:8080"), _fixture.Destination.OwnerId)));
        Assert.Equal(new QueueStatus(0, 0), _fixture.Open().Status());
    }

    [Fact]
    public void QueueRotatesAttemptsSoLaterTracksCanMakeProgress()
    {
        var queue = _fixture.Open();
        queue.Accept(QueueFixture.Submission(Enumerable.Range(0, 500).Select(_ => QueueFixture.Snapshot()).ToArray()));
        var later = QueueFixture.Snapshot();
        queue.Accept(new HubSubmission(QueueFixture.Collector(),
            new TrackDeclaration("other.data", 1, "range", "explicit"), [later]));
        var first = queue.TakePending();
        var second = queue.TakePending();
        Assert.Equal(500, first.Count);
        Assert.Contains(second, item => item.Record.Id == later.Id);
    }

    [Fact]
    public void PointRecordAndRouteAreAcceptedTogetherWithoutBackendIds()
    {
        var queue = _fixture.Open();
        var point = QueueFixture.Snapshot() with
        {
            EndedAt = null,
            Value = JsonSerializer.SerializeToElement(new { any = 42 }),
        };

        queue.Accept(new HubSubmission(QueueFixture.Collector(),
            new TrackDeclaration("example.unknown", 7, "point", null), [point]));

        var pending = Assert.Single(_fixture.Open().TakePending());
        Assert.Equal("example.unknown", pending.Route.Track.Type);
        Assert.Null(pending.Route.BackendTrackId);
        Assert.Null(pending.Record.EndedAt);
    }

    [Fact]
    public void DisplayNameDoesNotCreateAnotherLogicalRoute()
    {
        var queue = _fixture.Open();
        queue.Accept(QueueFixture.Submission(QueueFixture.Snapshot()));
        var next = QueueFixture.Snapshot();
        queue.Accept(new HubSubmission(QueueFixture.Collector("Renamed Mac"), QueueFixture.Track(), [next]));

        var records = queue.TakePending();
        Assert.Equal(2, records.Count);
        Assert.Single(records.Select(record => record.Route.Id).Distinct());
        Assert.All(records, record => Assert.Equal("Renamed Mac", record.Route.Collector.DisplayName));
    }

    public void Dispose() => _fixture.Dispose();
}
