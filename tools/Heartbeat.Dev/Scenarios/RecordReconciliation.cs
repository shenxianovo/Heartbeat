using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Hub;
using Microsoft.Data.Sqlite;

namespace Heartbeat.Dev;

internal sealed record ScenarioRecord(
    [property: JsonRequired] Guid OwnerId,
    [property: JsonRequired] string CollectorKey,
    [property: JsonRequired] string Target,
    [property: JsonRequired] TrackDeclaration Track,
    [property: JsonRequired] Guid TrackId,
    [property: JsonRequired] RecordSnapshot Record);

internal sealed record HubCustodySnapshot(Guid HubId, ScenarioRecord[] Records)
{
    private const string DatabaseFileName = "hub.sqlite";
    public static QueueStatus Status(string directory, DeliveryDestination destination) =>
        new RecordOutbox(Path.Combine(directory, DatabaseFileName), destination).Status();

    public static HubCustodySnapshot Read(string directory, DeliveryDestination destination)
    {
        var path = Path.Combine(directory, DatabaseFileName);
        if (!File.Exists(path)) throw new InvalidDataException("The Hub custody database is missing.");
        var hubId = Guid.Parse(File.ReadAllText(Path.Combine(directory, "hub-id")));
        var temporary = Directory.CreateTempSubdirectory("heartbeat-custody-").FullName;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try { return ReadCopy(path, temporary, hubId, destination); }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static HubCustodySnapshot ReadCopy(string path, string temporary, Guid hubId, DeliveryDestination destination)
    {
        // SQLite's online backup includes committed WAL data without changing the running queue.
        var copyPath = Path.Combine(temporary, DatabaseFileName);
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var copy = new SqliteConnection($"Data Source={copyPath};Pooling=False"))
        {
            source.Open();
            copy.Open();
            source.BackupDatabase(copy);
        }
        var queue = new RecordOutbox(copyPath, destination);
        var status = queue.Status();
        var records = queue.TakePending().Select(item => new ScenarioRecord(destination.OwnerId,
            item.Route.Collector.Key!, item.Route.Collector.Target!, item.Route.Track,
            item.Route.BackendTrackId ?? Guid.Empty, item.Record)).ToArray();
        if (hubId == Guid.Empty || status.Failed != 0 || records.Length != status.Pending)
            throw new InvalidDataException("Custody must contain only retryable Records within one snapshot batch.");
        return new(hubId, records);
    }
}

internal static class RecordReconciliation
{
    public static async Task<ScenarioRecord[]> ReadDatabaseAsync(ScenarioEnvironment environment, CancellationToken token) =>
        JsonSerializer.Deserialize<ScenarioRecord[]>(await environment.QueryDatabaseAsync(DatabaseQuery, token), JsonSerializerOptions.Web)!;

    public static void RequireSameRecords(ScenarioRecord[] expected, ScenarioRecord[] actual)
    {
        if (expected.Length == 0 || actual.Length != expected.Length
            || actual.Select(item => item.Record.Id).Distinct().Count() != actual.Length)
            throw new InvalidOperationException("Recovery lost or duplicated accepted Records.");
        var byId = actual.ToDictionary(item => item.Record.Id);
        foreach (var before in expected)
        {
            if (!byId.TryGetValue(before.Record.Id, out var after)
                || before.OwnerId != after.OwnerId || before.CollectorKey != after.CollectorKey
                || before.Target != after.Target || before.Track != after.Track
                || (before.TrackId != Guid.Empty && before.TrackId != after.TrackId))
                throw new InvalidOperationException($"Recovery changed Record identity or routing: {before.Record.Id}.");
            RequireSameSnapshot(before.Record, after.Record);
        }
    }

    private static void RequireSameSnapshot(RecordSnapshot before, RecordSnapshot after)
    {
        // PostgreSQL stores microseconds; native/.NET timestamps can carry a final 100ns digit.
        static long? Micros(DateTimeOffset? at) => at?.UtcTicks / 10;
        if (Micros(before.StartedAt) != Micros(after.StartedAt)
            || Micros(before.EndedAt) != Micros(after.EndedAt)
            || Micros(before.ObservedAt) != Micros(after.ObservedAt)
            || !JsonElement.DeepEquals(before.Value, after.Value))
            throw new InvalidOperationException($"Recovery changed accepted Record content or time: {before.Id}.");
    }

    public static object Summary(ScenarioRecord[] records) => records.Select(item => new
    {
        item.Record.Id, item.TrackId, item.Record.StartedAt, item.Record.EndedAt, item.Track,
    }).ToArray();

    // Read every row, not just matching IDs: extra/wrongly routed Records must fail reconciliation.
    public const string DatabaseQuery = """
        select coalesce(json_agg(json_build_object(
            'ownerId', l.owner_id, 'collectorKey', c.key, 'target', c.target,
            'track', json_build_object('type', t.type, 'version', t.version,
                'timeMode', t.time_mode, 'endMode', t.end_mode),
            'trackId', t.id,
            'record', json_build_object('id', r.id, 'startedAt', r.started_at,
                'endedAt', r.ended_at, 'observedAt', r.observed_at, 'value', r.value)
        ) order by r.id), '[]'::json)
        from records r join tracks t on t.id = r.track_id
        join collectors c on c.id = t.collector_id join timelines l on l.id = c.timeline_id;
        """;
}
