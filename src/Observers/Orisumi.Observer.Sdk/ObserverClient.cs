using System.Net;
using System.Text;
using System.Text.Json;
using Orisumi.Core;
using Microsoft.Data.Sqlite;

namespace Orisumi.Observer.Sdk;

public sealed class ObserverClient : IDisposable
{
    private readonly string _connectionString;
    private readonly HttpClient _http;
    private readonly object _localGate = new();
    private readonly SemaphoreSlim _sender = new(1);
    private int _batchLimit = 500;
    private bool _disposed;

    public ObserverClient(string path, EntityId instanceId, HttpClient http)
    {
        _http = http;
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 30 }.ToString();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS local_metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS latest(entity_id TEXT PRIMARY KEY, version_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS local_versions(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, version_id TEXT NOT NULL UNIQUE,
                entity_id TEXT NOT NULL, payload TEXT NOT NULL, state TEXT NOT NULL,
                strategy TEXT NOT NULL, freeze_order INTEGER, rule TEXT, detail TEXT);
            CREATE INDEX IF NOT EXISTS ix_local_versions_delivery ON local_versions(state,freeze_order);
            INSERT OR IGNORE INTO local_metadata(key,value) VALUES('freeze_order','0');
            """;
        command.ExecuteNonQuery();
        var local = new LocalTransaction(connection, transaction);
        var owner = local.Metadata("instance");
        if (owner is not null && owner != instanceId.Value.ToString()) throw new InvalidDataException("The SQLite file belongs to another program instance.");
        local.Metadata("instance", instanceId.Value.ToString());
        transaction.Commit();
    }

    public T Execute<T>(Func<LocalTransaction, T> operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_localGate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var result = operation(new(connection, transaction));
            transaction.Commit();
            return result;
        }
    }
    public void Execute(Action<LocalTransaction> operation) => Execute(local => { operation(local); return true; });

    public async Task<VersionId> RegisterSchemaAsync(EntityId id, EntitySchema schema, CancellationToken cancellationToken = default)
    {
        await _sender.WaitAsync(cancellationToken);
        try
        {
            var chain = await TryReadChainAsync(id, cancellationToken);
            return Execute(local =>
            {
                if (chain is not null)
                {
                    foreach (var saved in chain) local.Remember(saved);
                    var match = chain.FirstOrDefault(saved => SameContent(saved.Submission, schema, null));
                    if (match is not null) return match.VersionId;
                }
                var existing = local.Versions().FirstOrDefault(version => version.Version.EntityId == id && SameContent(version.Version, schema, null));
                if (existing is not null) return existing.Version.VersionId;
                var versionId = local.Draft(id, schema);
                local.Complete(id);
                local.Metadata("schema:" + versionId.Value, "pending");
                return versionId;
            });
        }
        finally { _sender.Release(); }
    }

    public VersionId RegisterInstance(EntityId id, VersionId schemaVersionId, BusinessContent content) => Execute(local =>
    {
        schemaVersionId = local.Resolve(schemaVersionId);
        var latest = local.Latest(id);
        if (latest is not null && SameContent(latest.Version, content, schemaVersionId)) return local.Reference(id);
        var version = local.Draft(id, content, schemaVersionId);
        local.Complete(id);
        return version;
    });

    public Task<HttpResponseMessage> GetEntityResponseAsync(EntityId id, CancellationToken cancellationToken = default) =>
        _http.GetAsync("entities/" + id.Value, cancellationToken);

    public Task<EntityVersion?> ReadCurrentAsync(EntityId id, CancellationToken cancellationToken = default) => ReadAsync("entities/" + id.Value, cancellationToken);
    public Task<EntityVersion?> ReadVersionAsync(VersionId id, CancellationToken cancellationToken = default) => ReadAsync("versions/" + id.Value, cancellationToken);

    private async Task<EntityVersion?> ReadAsync(string path, CancellationToken token)
    {
        using var response = await _http.GetAsync(path, token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return VersionJson.ReadSaved(json.RootElement);
    }

    public DeliveryStatus Status => Execute(local =>
    {
        var versions = local.Versions();
        return new DeliveryStatus(versions.Count(version => version.State is SubmissionState.Frozen or SubmissionState.Rejected),
            versions.Count(version => version.State == SubmissionState.Draft), versions.Count(version => version.State == SubmissionState.Saved),
            local.Rejection(), local.Metadata("error"));
    });

    public async Task RunAsync(TimeSpan retryInterval, Action<DeliveryStatus>? report = null, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SendAsync(cancellationToken);
            report?.Invoke(Status);
            await Task.Delay(retryInterval, cancellationToken);
        }
    }

    public async Task SendAsync(CancellationToken cancellationToken = default)
    {
        await _sender.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                if (Status is { Rejection: not null } or { Error: not null }) return;
                if (!await ReconcileSchemasAsync(cancellationToken)) return;
                var batch = Execute(local =>
                {
                    foreach (var draft in local.Versions().Where(version => version.State == SubmissionState.Draft && version.Strategy == SubmissionStrategy.Immediate))
                        local.Freeze(draft.Version.VersionId);
                    return SelectBatch(local.Versions());
                });
                if (batch.Count == 0) return;
                var payload = JsonSerializer.SerializeToUtf8Bytes(new { versions = batch.Select(version => VersionJson.Submission(version.Version)) });
                using var body = new ByteArrayContent(payload);
                body.Headers.ContentType = new("application/json");
                using var response = await _http.PostAsync("versions", body, cancellationToken);
                if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                {
                    if (batch.Count == 1) { Fail("One version exceeds the server request size limit."); return; }
                    _batchLimit = Math.Max(1, batch.Count / 2);
                    continue;
                }
                if ((int)response.StatusCode >= 500) return;
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var confirmed = json.RootElement.GetProperty("versions").EnumerateArray().ToArray();
                    if (confirmed.Length != batch.Count || confirmed.Where((value, index) => value.GetProperty("versionId").GetGuid() != batch[index].Version.VersionId.Value).Any())
                    { Fail("The server returned an invalid confirmation list."); return; }
                    foreach (var value in confirmed) _ = VersionJson.ParseTime(value.GetProperty("recordedAt").GetString()!);
                    Execute(local => { foreach (var version in batch) local.Confirm(version.Version.VersionId); });
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict
                    && json.RootElement.TryGetProperty("index", out var rejectedIndex))
                {
                    var index = rejectedIndex.GetInt32();
                    if (index < 0 || index >= batch.Count || json.RootElement.GetProperty("versionId").GetGuid() != batch[index].Version.VersionId.Value)
                    { Fail("The server returned an invalid rejection index."); return; }
                    var rule = json.RootElement.GetProperty("rule").GetString()!;
                    var detail = json.RootElement.GetProperty("detail").GetString()!;
                    Execute(local =>
                    {
                        foreach (var version in batch.Take(index)) local.Confirm(version.Version.VersionId);
                        local.Reject(batch[index].Version.VersionId, rule, detail);
                    });
                    return;
                }
                Fail($"Submission failed without a version result: HTTP {(int)response.StatusCode}.");
                return;
            }
        }
        catch (HttpRequestException) { /* Leave frozen payloads intact for the next attempt. */ }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { /* HTTP timeout: outcome is unknown. */ }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        { Fail("Invalid submission response: " + exception.Message); }
        finally { _sender.Release(); }
    }

    private List<LocalVersion> SelectBatch(IReadOnlyList<LocalVersion> versions)
    {
        var batch = new List<LocalVersion>();
        var bytes = Encoding.UTF8.GetByteCount("{\"versions\":[]}");
        foreach (var version in versions.Where(version => version.State == SubmissionState.Frozen).OrderBy(version => version.FreezeOrder))
        {
            var size = Encoding.UTF8.GetByteCount(VersionJson.Submission(version.Version).GetRawText()) + (batch.Count == 0 ? 0 : 1);
            if (batch.Count > 0 && (batch.Count >= _batchLimit || bytes + size > 10 * 1024 * 1024)) break;
            batch.Add(version);
            bytes += size;
        }
        return batch;
    }

    private async Task<bool> ReconcileSchemasAsync(CancellationToken token)
    {
        var schemas = Execute(local => local.Versions().Where(version => version.State == SubmissionState.Frozen
            && version.Version.Content is EntitySchema && local.Metadata("schema:" + version.Version.VersionId.Value) == "pending").ToArray());
        foreach (var schema in schemas)
        {
            var chain = await TryReadChainAsync(schema.Version.EntityId, token);
            if (chain is null) return false;
            Execute(local =>
            {
                foreach (var saved in chain) local.Remember(saved);
                var match = chain.FirstOrDefault(saved => SameContent(saved.Submission, schema.Version.Content, null));
                if (match is not null)
                {
                    if (match.VersionId == schema.Version.VersionId) local.Confirm(match.VersionId);
                    else local.ReplaceWithSaved(schema.Version.VersionId, match);
                }
                else local.RebaseUnsent(schema.Version.VersionId,
                    local.PendingSchemaPredecessor(schema) ?? (chain.Count == 0 ? null : chain[^1].VersionId));
                local.SetRegistrationHead(schema.Version.EntityId, chain.Count == 0 ? null : chain[^1].VersionId);
                local.Metadata("schema:" + schema.Version.VersionId.Value, "ready");
            });
        }
        return true;
    }

    private async Task<IReadOnlyList<EntityVersion>?> TryReadChainAsync(EntityId id, CancellationToken token)
    {
        try
        {
            using var response = await _http.GetAsync("entities/" + id.Value + "/versions", token);
            if (response.StatusCode == HttpStatusCode.NotFound) return [];
            // A deleted schema will be rejected on submission, so business code receives the rule.
            if (response.StatusCode == HttpStatusCode.Gone) return [];
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            return json.RootElement.GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSaved).ToArray();
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    private static bool SameContent(VersionSubmission version, VersionContent content, VersionId? schema) =>
        VersionRules.SameSubmission(version, version with { Content = content, SchemaVersionId = schema });
    private void Fail(string error) => Execute(local => local.Metadata("error", error));
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous = FULL";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sender.Dispose();
    }
}
