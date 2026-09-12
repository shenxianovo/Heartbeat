using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Heartbeat.Hub;

public sealed class RecordOutbox
{
    public const int MaximumBatchSize = 500;
    public const int MaximumBatchBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly int _maximumRecords;

    public RecordOutbox(string path, DeliveryDestination destination, int maximumRecords = 10000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRecords, 1);
        Destination = destination;
        _maximumRecords = maximumRecords;
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();

        using var connection = Open();
        using (var wal = Command(connection, null, "PRAGMA journal_mode=WAL;"))
        {
            if (!string.Equals(wal.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The queue requires SQLite WAL mode.");
            }
        }

        InitializeSchema(connection, destination);
    }

    public DeliveryDestination Destination { get; }

    public IReadOnlyList<RecordSnapshot> Accept(HubSubmission submission)
    {
        var normalized = Normalize(submission);
        if (JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions).Length > MaximumBatchBytes)
        {
            throw new ArgumentException("The normalized submission exceeds 1 MiB.");
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var route = ResolveLocalRoute(connection, transaction, normalized.Collector!, normalized.Track!);
        using var count = Command(connection, transaction, "SELECT COUNT(*) FROM records;");
        var total = (long)count.ExecuteScalar()!;
        foreach (var record in normalized.Records!.Cast<RecordSnapshot>())
        {
            var existing = Find(connection, transaction, record.Id);
            if (existing is not null)
            {
                if (existing.Route.Id != route.Id || !HasSameFixedFields(existing.Record, record))
                {
                    throw new RecordConflictException();
                }

                if (Confirms(existing.Record.EndedAt, record.EndedAt))
                {
                    continue;
                }
            }
            else if (++total > _maximumRecords)
            {
                throw new QueueCapacityException();
            }

            using var write = Command(connection, transaction, """
                INSERT INTO records(id, route_id, snapshot) VALUES ($id, $route, $snapshot)
                ON CONFLICT(id) DO UPDATE SET snapshot = excluded.snapshot;
                """, ("$id", record.Id.ToString()), ("$route", route.Id),
                ("$snapshot", JsonSerializer.Serialize(record, JsonOptions)));
            write.ExecuteNonQuery();
        }

        transaction.Commit();
        return normalized.Records!.Select(record => record!).ToArray();
    }

    public IReadOnlyList<PendingRecord> TakePending()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var select = Command(connection, transaction, PendingSelect + """

            WHERE r.failure IS NULL
            ORDER BY r.attempted_at, r.id LIMIT $limit;
            """, ("$limit", MaximumBatchSize));
        var records = Read(select);
        foreach (var record in records)
        {
            using var update = Command(connection, transaction,
                "UPDATE records SET attempted_at = $at WHERE id = $id;",
                ("$at", DateTimeOffset.UtcNow.UtcTicks), ("$id", record.Record.Id.ToString()));
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return records;
    }

    public void SaveMapping(DeliveryRoute sent, Guid backendCollectorId, Guid backendTrackId)
    {
        if (backendCollectorId == Guid.Empty || backendTrackId == Guid.Empty)
        {
            throw new ArgumentException("Backend Collector and Track identities are required.");
        }

        using var connection = Open();
        using var command = Command(connection, null, """
            UPDATE routes
            SET backend_collector_id = $collector_id, backend_track_id = $track_id
            WHERE id = $id AND collector_key = $key AND target = $target
              AND track_type = $type AND track_version = $version
              AND time_mode = $time_mode AND end_mode IS $end_mode;
            """, ("$collector_id", backendCollectorId.ToString()), ("$track_id", backendTrackId.ToString()),
            ("$id", sent.Id), ("$key", sent.Collector.Key), ("$target", sent.Collector.Target),
            ("$type", sent.Track.Type), ("$version", sent.Track.Version),
            ("$time_mode", sent.Track.TimeMode), ("$end_mode", sent.Track.EndMode));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The local delivery route changed before its mapping was saved.");
        }
    }

    public void Apply(IReadOnlyList<DeliveryOutcome> outcomes)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var outcome in outcomes)
        {
            var current = Find(connection, transaction, outcome.Sent.Record.Id);
            if (current is null || current.Route.Id != outcome.Sent.Route.Id ||
                !HasSameFixedFields(current.Record, outcome.Sent.Record) ||
                current.Record.EndedAt != outcome.Sent.Record.EndedAt)
            {
                continue;
            }

            if (!outcome.Stored && string.IsNullOrWhiteSpace(outcome.Failure))
            {
                continue;
            }

            using var update = Command(connection, transaction, outcome.Stored
                ? "DELETE FROM records WHERE id = $id;"
                : "UPDATE records SET failure = $failure WHERE id = $id;",
                ("$id", outcome.Sent.Record.Id.ToString()), ("$failure", outcome.Failure));
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public QueueStatus Status()
    {
        using var connection = Open();
        using var command = Command(connection, null,
            "SELECT COUNT(*) - COUNT(failure), COUNT(failure) FROM records;");
        using var reader = command.ExecuteReader();
        reader.Read();
        return new QueueStatus(reader.GetInt64(0), reader.GetInt64(1));
    }

    public IReadOnlyList<PendingRecord> ReadFailures()
    {
        using var connection = Open();
        using var command = Command(connection, null, PendingSelect + """

            WHERE r.failure IS NOT NULL
            ORDER BY r.id LIMIT $limit;
            """, ("$limit", MaximumBatchSize));
        return Read(command);
    }

    internal static int EncodedSize(RecordSnapshot record) =>
        JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions).Length;

    private const string PendingSelect = """
        SELECT r.snapshot, r.failure,
               s.id, s.collector_key, s.target, s.display_name,
               s.track_type, s.track_version, s.time_mode, s.end_mode,
               s.backend_collector_id, s.backend_track_id
        FROM records r
        JOIN routes s ON s.id = r.route_id
        """;

    private static HubSubmission Normalize(HubSubmission? submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var collector = submission.Collector ?? throw new ArgumentException("A Collector declaration is required.");
        var track = submission.Track ?? throw new ArgumentException("A Track declaration is required.");
        var key = Required(collector.Key, "Collector key", 255);
        var target = Required(collector.Target, "Collector target", 255);
        var displayName = Required(collector.DisplayName, "Collector display name", 255);
        var segments = key.Split('.');
        if (segments.Length < 2 || segments.Any(segment => segment.Length == 0) ||
            key.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9')
                and not '.' and not '-' and not '_'))
        {
            throw new ArgumentException("A Collector key must be a lowercase, dot-separated identifier.");
        }

        var type = Required(track.Type, "Track type");
        ArgumentOutOfRangeException.ThrowIfLessThan(track.Version, 1);
        var timeMode = track.TimeMode?.Trim();
        var endMode = track.EndMode?.Trim();
        if ((timeMode == "point" && endMode is not null) ||
            (timeMode == "range" && endMode is not ("explicit" or "next_record")) ||
            timeMode is not ("point" or "range"))
        {
            throw new ArgumentException("A Track must be point, range + explicit, or range + next_record.");
        }

        if (submission.Records is null || submission.Records.Count is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentException("A submission must contain 1 to 500 Records.");
        }

        var records = submission.Records.Select(record => NormalizeRecord(record, timeMode, endMode)).ToArray();
        return new HubSubmission(new CollectorDeclaration(key, target, displayName),
            new TrackDeclaration(type, track.Version, timeMode, endMode), records);
    }

    private static RecordSnapshot NormalizeRecord(RecordSnapshot? record, string timeMode, string? endMode)
    {
        if (record is null || record.Id == Guid.Empty || record.Id.Version != 7 || record.StartedAt is null)
        {
            throw new ArgumentException("A Record requires a UUID v7 and start time.");
        }

        var expectsEnd = timeMode == "range" && endMode == "explicit";
        if ((record.EndedAt is not null) != expectsEnd || record.EndedAt < record.StartedAt)
        {
            throw new ArgumentException("Record end time does not match its Track declaration.");
        }

        if (record.Value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("A Record value is required.");
        }

        var startedAt = record.StartedAt.Value.ToUniversalTime();
        var observedAt = record.ObservedAt?.ToUniversalTime();
        return record with
        {
            StartedAt = startedAt,
            EndedAt = record.EndedAt?.ToUniversalTime(),
            ObservedAt = observedAt == startedAt ? null : observedAt,
            Value = record.Value.Clone(),
        };
    }

    private static string Required(string? value, string name, int? max = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var normalized = value.Trim();
        if (max is { } limit && normalized.Length > limit)
        {
            throw new ArgumentException($"{name} cannot exceed {limit} characters.");
        }

        return normalized;
    }

    private static DeliveryRoute ResolveLocalRoute(
        SqliteConnection connection, SqliteTransaction transaction,
        CollectorDeclaration collector, TrackDeclaration track)
    {
        using (var insert = Command(connection, transaction, """
            INSERT INTO routes(collector_key, target, display_name, track_type, track_version, time_mode, end_mode)
            VALUES ($key, $target, $display_name, $type, $version, $time_mode, $end_mode)
            ON CONFLICT(collector_key, target, track_type, track_version)
            DO UPDATE SET display_name = excluded.display_name;
            """, ("$key", collector.Key), ("$target", collector.Target), ("$display_name", collector.DisplayName),
            ("$type", track.Type), ("$version", track.Version), ("$time_mode", track.TimeMode), ("$end_mode", track.EndMode)))
        {
            insert.ExecuteNonQuery();
        }

        using var select = Command(connection, transaction, """
            SELECT id, display_name, time_mode, end_mode, backend_collector_id, backend_track_id
            FROM routes WHERE collector_key = $key AND target = $target
              AND track_type = $type AND track_version = $version;
            """, ("$key", collector.Key), ("$target", collector.Target),
            ("$type", track.Type), ("$version", track.Version));
        using var reader = select.ExecuteReader();
        if (!reader.Read() || reader.GetString(2) != track.TimeMode || GetNullableString(reader, 3) != track.EndMode)
        {
            throw new RecordConflictException("The Track declaration conflicts with its existing local route.");
        }

        return new DeliveryRoute(reader.GetInt64(0), collector with { DisplayName = reader.GetString(1) }, track,
            GetNullableGuid(reader, 4), GetNullableGuid(reader, 5));
    }

    private static PendingRecord? Find(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = Command(connection, transaction, PendingSelect + "\nWHERE r.id = $id;",
            ("$id", id.ToString()));
        return Read(command).SingleOrDefault();
    }

    private static List<PendingRecord> Read(SqliteCommand command)
    {
        var results = new List<PendingRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var route = new DeliveryRoute(reader.GetInt64(2),
                new CollectorDeclaration(reader.GetString(3), reader.GetString(4), reader.GetString(5)),
                new TrackDeclaration(reader.GetString(6), reader.GetInt32(7), reader.GetString(8), GetNullableString(reader, 9)),
                GetNullableGuid(reader, 10), GetNullableGuid(reader, 11));
            results.Add(new PendingRecord(route,
                JsonSerializer.Deserialize<RecordSnapshot>(reader.GetString(0), JsonOptions)!,
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return results;
    }

    private static bool HasSameFixedFields(RecordSnapshot left, RecordSnapshot right) =>
        left.Id == right.Id && left.StartedAt == right.StartedAt && left.ObservedAt == right.ObservedAt &&
        JsonElement.DeepEquals(left.Value, right.Value);

    private static bool Confirms(DateTimeOffset? current, DateTimeOffset? offered) =>
        current is null ? offered is null : offered is not null && current >= offered;

    private static Guid? GetNullableGuid(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void InitializeSchema(SqliteConnection connection, DeliveryDestination destination)
    {
        using var transaction = connection.BeginTransaction();
        using var schema = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS destination (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                backend_url TEXT NOT NULL, owner_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS routes (
                id INTEGER PRIMARY KEY,
                collector_key TEXT NOT NULL, target TEXT NOT NULL, display_name TEXT NOT NULL,
                track_type TEXT NOT NULL, track_version INTEGER NOT NULL,
                time_mode TEXT NOT NULL, end_mode TEXT,
                backend_collector_id TEXT, backend_track_id TEXT,
                UNIQUE(collector_key, target, track_type, track_version),
                CHECK ((time_mode = 'point' AND end_mode IS NULL) OR
                       (time_mode = 'range' AND end_mode IN ('explicit', 'next_record'))));
            CREATE TABLE IF NOT EXISTS records (
                id TEXT PRIMARY KEY,
                route_id INTEGER NOT NULL REFERENCES routes(id) ON DELETE RESTRICT,
                snapshot TEXT NOT NULL, failure TEXT,
                attempted_at INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS pending_records ON records(failure, attempted_at, id);
            INSERT INTO destination(singleton, backend_url, owner_id)
            VALUES (1, $url, $owner) ON CONFLICT(singleton) DO NOTHING;
            """, ("$url", destination.BackendUrl.AbsoluteUri), ("$owner", destination.OwnerId.ToString()));
        schema.ExecuteNonQuery();

        using var binding = Command(connection, transaction,
            "SELECT backend_url, owner_id FROM destination WHERE singleton = 1;");
        using var reader = binding.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != destination.BackendUrl.AbsoluteUri ||
            reader.GetString(1) != destination.OwnerId.ToString())
        {
            throw new InvalidOperationException("The SQLite file belongs to a different backend or Owner.");
        }

        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = Command(connection, null, "PRAGMA synchronous=FULL;");
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        return command;
    }
}
