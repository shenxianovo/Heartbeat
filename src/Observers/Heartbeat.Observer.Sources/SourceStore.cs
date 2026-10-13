using System.Text.Json;
using Heartbeat.Application.Entities;
using Heartbeat.Core;
using Microsoft.Data.Sqlite;

namespace Heartbeat.Observers.Sources;

public sealed record SourceSubmission(Guid Id, string Category, string Resource, JsonElement Body, Guid? SchemaId = null);
public sealed record SourceReceipt(Guid ObservationId, Guid ContentId, Guid SourceEntityId, bool Replayed);
public sealed record PendingSource(long Sequence, Guid ObservationId, SourceSubmission[] Entities, string? LastError);

/// <summary>One collector instance owns this durable identity map, immutable history, and outbox.</summary>
public sealed class SourceStore : IDisposable
{
    private static readonly SourceSchema IdentitySchema = new(
        Guid.Parse("019a1280-0000-7000-8000-000000000001"), "source-identities", "Source identity",
        SourceFields.Of(new { kind = SourceFields.String(), scope = SourceFields.String(), nativeKey = SourceFields.String() }));
    private readonly SqliteConnection _connection;
    private readonly FileStream _lease;
    private bool _disposed;
    public Guid ObserverId { get; }

    public SourceStore(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString());
        try
        {
            _connection.Open();
            Execute(null, "PRAGMA synchronous = FULL;");
            using var transaction = _connection.BeginTransaction();
            Execute(transaction, """
                CREATE TABLE IF NOT EXISTS source_owner (id INTEGER PRIMARY KEY CHECK (id=1), observer_id TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS source_definitions (id TEXT PRIMARY KEY, resource TEXT NOT NULL UNIQUE, fields TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS source_keys (key TEXT PRIMARY KEY, entity_id TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS source_captures (key TEXT PRIMARY KEY, captured_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS source_evidence (key TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, observation_id TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS source_outbox (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT, observation_id TEXT NOT NULL UNIQUE,
                    payload TEXT NOT NULL, last_error TEXT);
                INSERT OR IGNORE INTO source_owner (id,observer_id) VALUES (1,$id);
                """, ("$id", EntityId.New().Value.ToString()));
            ObserverId = Guid.Parse((string)Scalar(transaction, "SELECT observer_id FROM source_owner WHERE id=1")!);
            if (!ValidId(ObserverId)) throw new InvalidDataException("Stored Observer identity is not UUIDv7; state was not reset.");
            transaction.Commit();
        }
        catch { _connection.Dispose(); _lease.Dispose(); throw; }
    }

    public DateTimeOffset CaptureTime(string scope, string captureKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(captureKey);
        using var transaction = _connection.BeginTransaction();
        var key = Key("capture", scope, captureKey);
        Execute(transaction, "INSERT OR IGNORE INTO source_captures (key,captured_at) VALUES ($key,$time)",
            ("$key", key), ("$time", DateTimeOffset.UtcNow.ToString("O")));
        var time = DateTimeOffset.Parse((string)Scalar(transaction, "SELECT captured_at FROM source_captures WHERE key=$key", ("$key", key))!,
            System.Globalization.CultureInfo.InvariantCulture);
        transaction.Commit();
        return time;
    }

    public SourceReceipt Enqueue(string scope, SourceRecord record) => EnqueueBatch(scope, [record]).Single();

    public IReadOnlyList<SourceReceipt> EnqueueBatch(string scope, IReadOnlyList<SourceRecord> records, CancellationToken cancellationToken = default)
    {
        using var transaction = _connection.BeginTransaction();
        var results = new List<SourceReceipt>();
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(EnqueueCore(transaction, scope, record));
        }
        transaction.Commit();
        return results;
    }

    private SourceReceipt EnqueueCore(SqliteTransaction transaction, string scope, SourceRecord record)
    {
        Validate(scope, record);
        RegisterDefinition(transaction, IdentitySchema);
        RegisterDefinition(transaction, record.Schema);
        var entities = new List<SourceSubmission>
        {
            new(ObserverId, "observer", "observers", Body(new { }, new { name = "Source observer" })),
            SchemaSubmission(IdentitySchema), SchemaSubmission(record.Schema),
        };
        Guid Identity(string kind, string nativeKey)
        {
            var id = Resolve(transaction, Key("identity", scope, kind, nativeKey));
            entities.Add(new(id, "entity", IdentitySchema.ResourceName,
                Body(new { }, new { kind, scope, nativeKey }), IdentitySchema.Id));
            return id;
        }
        var sourceId = Identity(record.Kind, record.NativeKey);
        var references = new Dictionary<string, Guid>(StringComparer.Ordinal) { ["sourceEntityId"] = sourceId };
        foreach (var reference in record.References)
            references.Add(reference.Key, Identity(reference.Value.Kind, reference.Value.NativeKey));
        var contentBody = Body(references, record.Properties);
        if (!EntitySchemaRules.ValidEntity(record.Schema.Fields, contentBody.GetProperty("references"), record.Properties))
            throw new InvalidDataException("Native adapter output does not match its declared source schema.");
        if (System.Text.Encoding.UTF8.GetByteCount(contentBody.GetRawText()) > 10 * 1024 * 1024)
            throw new InvalidDataException("Mapped entity exceeds the API 10 MiB request limit.");
        var contentId = Resolve(transaction, Key("content", record.Schema.Id.ToString(), SourceJson.Canonical(contentBody)));
        var observationBody = Body(new { observerId = ObserverId, contentId, schemaId = record.Schema.Id },
            new { startAt = Microseconds(record.StartAt), endAt = Microseconds(record.EndAt), timeZone = record.TimeZone });
        var evidenceKey = Key("evidence", scope, record.Kind, record.NativeKey, record.EvidenceKey);
        var fingerprint = SourceJson.Hash(SourceJson.Canonical(observationBody) + SourceJson.Canonical(contentBody));
        var prior = Scalar(transaction, "SELECT fingerprint FROM source_evidence WHERE key=$key", ("$key", evidenceKey)) as string;
        if (prior is not null && prior != fingerprint)
            throw new InvalidDataException("The same source evidence key changed meaning or content. Keep the original and provide a new source revision key.");
        var observationId = Resolve(transaction, evidenceKey);
        entities.Add(new(contentId, "entity", record.Schema.ResourceName, contentBody, record.Schema.Id));
        entities.Add(new(observationId, "observation", "observations", observationBody));
        Execute(transaction, "INSERT OR IGNORE INTO source_evidence (key,fingerprint,observation_id) VALUES ($key,$hash,$id)",
            ("$key", evidenceKey), ("$hash", fingerprint), ("$id", observationId.ToString()));
        Execute(transaction, "INSERT OR IGNORE INTO source_outbox (observation_id,payload) VALUES ($id,$payload)",
            ("$id", observationId.ToString()), ("$payload", JsonSerializer.Serialize(entities.DistinctBy(e => e.Id), JsonSerializerOptions.Web)));
        return new(observationId, contentId, sourceId, prior is not null);
    }

    public IReadOnlyList<PendingSource> Pending()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT sequence,observation_id,payload,last_error FROM source_outbox ORDER BY sequence";
        using var reader = command.ExecuteReader();
        var records = new List<PendingSource>();
        while (reader.Read()) records.Add(new(reader.GetInt64(0), Guid.Parse(reader.GetString(1)),
            JsonSerializer.Deserialize<SourceSubmission[]>(reader.GetString(2), JsonSerializerOptions.Web)
                ?? throw new InvalidDataException("Corrupt source outbox; no record was discarded."),
            reader.IsDBNull(3) ? null : reader.GetString(3)));
        return records;
    }

    public void Confirm(long sequence) => Execute(null, "DELETE FROM source_outbox WHERE sequence=$sequence", ("$sequence", sequence));
    public void RecordError(long sequence, string? error) => Execute(null,
        "UPDATE source_outbox SET last_error=$error WHERE sequence=$sequence", ("$sequence", sequence), ("$error", (object?)error ?? DBNull.Value));

    private static void Validate(string scope, SourceRecord record)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(record.Kind)
            || string.IsNullOrWhiteSpace(record.NativeKey) || string.IsNullOrWhiteSpace(record.EvidenceKey))
            throw new InvalidDataException("Scope, source kind, native identity and evidence key are required.");
        if (!ValidId(record.Schema.Id) || !EntitySchemaRules.ValidResource(record.Schema.ResourceName)
            || !EntitySchemaRules.ValidFields(record.Schema.Fields)) throw new InvalidDataException("Invalid source schema.");
        SourceJson.Validate(record.Properties);
        if (record.References.Values.Any(r => string.IsNullOrWhiteSpace(r.Kind) || string.IsNullOrWhiteSpace(r.NativeKey)))
            throw new InvalidDataException("Referenced source identities require kind and native key.");
        if (record.References.ContainsKey("sourceEntityId")) throw new InvalidDataException("sourceEntityId is maintained by the collector.");
        if (record.StartAt > record.EndAt || record.TimeZone is not null &&
            (!TimeZoneInfo.TryFindSystemTimeZoneById(record.TimeZone, out var zone) || !zone.HasIanaId))
            throw new InvalidDataException("Invalid source fact time or IANA zone.");
    }

    private void RegisterDefinition(SqliteTransaction transaction, SourceSchema schema)
    {
        var fields = SourceJson.Canonical(schema.Fields);
        var existing = Scalar(transaction, "SELECT fields FROM source_definitions WHERE id=$id", ("$id", schema.Id.ToString())) as string;
        if (existing is not null && (existing != fields ||
            (string?)Scalar(transaction, "SELECT resource FROM source_definitions WHERE id=$id", ("$id", schema.Id.ToString())) != schema.ResourceName))
            throw new InvalidDataException("A registered source schema changed structure or resource without a new identity.");
        var resourceOwner = Scalar(transaction, "SELECT id FROM source_definitions WHERE resource=$resource", ("$resource", schema.ResourceName)) as string;
        if (resourceOwner is not null && resourceOwner != schema.Id.ToString())
            throw new InvalidDataException("A source resource is already bound to another schema identity.");
        Execute(transaction, "INSERT OR IGNORE INTO source_definitions (id,resource,fields) VALUES ($id,$resource,$fields)",
            ("$id", schema.Id.ToString()), ("$resource", schema.ResourceName), ("$fields", fields));
    }

    private Guid Resolve(SqliteTransaction transaction, string key)
    {
        Execute(transaction, "INSERT OR IGNORE INTO source_keys (key,entity_id) VALUES ($key,$id)",
            ("$key", key), ("$id", EntityId.New().Value.ToString()));
        return Guid.Parse((string)Scalar(transaction, "SELECT entity_id FROM source_keys WHERE key=$key", ("$key", key))!);
    }
    private static bool ValidId(Guid id) => id.Version == 7 && (id.ToByteArray()[8] & 0xc0) == 0x80;
    private static DateTime? Microseconds(DateTimeOffset? value) => value is null ? null : new DateTime(value.Value.UtcTicks - value.Value.UtcTicks % 10, DateTimeKind.Utc);
    private static string Key(params string[] parts) => SourceJson.Hash(JsonSerializer.Serialize(parts));
    private static JsonElement Body(object references, object properties) => SourceFields.Of(new { references, properties });
    private static SourceSubmission SchemaSubmission(SourceSchema schema) => new(schema.Id, "entity_schema", "schemas",
        Body(new { }, new { name = schema.Name, resourceName = schema.ResourceName, fields = schema.Fields }));
    private object? Scalar(SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    { using var command = Command(transaction, sql, parameters); return command.ExecuteScalar(); }
    private void Execute(SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    { using var command = Command(transaction, sql, parameters); command.ExecuteNonQuery(); }
    private SqliteCommand Command(SqliteTransaction? transaction, string sql, (string Name, object Value)[] parameters)
    {
        var command = _connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _connection.Dispose(); _lease.Dispose(); GC.SuppressFinalize(this);
    }
}
