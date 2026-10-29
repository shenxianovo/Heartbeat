using System.Text.Json;
using System.Text.Json.Nodes;
using Orisumi.Core;
using Microsoft.Data.Sqlite;

namespace Orisumi.Observer.Runtime.Store;

/// <summary>All calls made through ObserverStore.Execute share one durable local transaction.</summary>
public sealed class LocalTransaction
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    internal LocalTransaction(SqliteConnection connection, SqliteTransaction transaction)
    { _connection = connection; _transaction = transaction; }

    public bool IsKnownDeleted(EntityId id) => Metadata("deleted:" + id.Value) == "true";
    public void RememberDeleted(EntityId id) => Metadata("deleted:" + id.Value, "true");
    public bool IsKnownMissing(VersionId id) => Metadata("missing:" + id.Value) == "true";
    public void RememberMissing(VersionId id) => Metadata("missing:" + id.Value, "true");

    public IReadOnlyList<LocalVersion> Versions() => Read("SELECT payload, state, strategy, freeze_order, rule, detail FROM local_versions ORDER BY sequence");
    public IReadOnlyList<LocalVersion> CurrentVersions() => Read("""
        SELECT v.payload, v.state, v.strategy, v.freeze_order, v.rule, v.detail
        FROM latest l JOIN local_versions v ON v.version_id = l.version_id ORDER BY v.sequence
        """);
    public LocalVersion? Find(VersionId id) => Read("SELECT payload, state, strategy, freeze_order, rule, detail FROM local_versions WHERE version_id = $id", ("$id", id.Value.ToString())).SingleOrDefault();
    public LocalVersion? Latest(EntityId id) => Read("""
        SELECT v.payload, v.state, v.strategy, v.freeze_order, v.rule, v.detail
        FROM latest l JOIN local_versions v ON v.version_id = l.version_id WHERE l.entity_id = $id
        """, ("$id", id.Value.ToString())).SingleOrDefault();

    public VersionId Draft(EntityId entityId, VersionContent content, VersionId? schemaVersionId = null,
        SubmissionStrategy strategy = SubmissionStrategy.Immediate, bool publishPreview = false)
    {
        if (publishPreview && content is not Observation) throw new ArgumentException("Only Observations have observation previews.", nameof(publishPreview));
        schemaVersionId = schemaVersionId is { } schema ? Resolve(schema) : null;
        var latest = Latest(entityId);
        var version = latest?.State == SubmissionState.Draft
            ? latest.Version with { Content = content, SchemaVersionId = schemaVersionId }
            : new VersionSubmission(entityId, VersionId.New(), latest?.Version.VersionId, schemaVersionId, content);
        version = VersionTime.Normalize(version);
        FreezeReferences(version, []);
        Put(version, SubmissionState.Draft, strategy, null);
        SetLatest(entityId, version.VersionId);
        if (publishPreview)
            Execute("""
                INSERT INTO observation_previews(entity_id,version_id,sequence,stopped) VALUES($entity,$version,0,0)
                ON CONFLICT(entity_id) DO UPDATE SET version_id=excluded.version_id,
                    stopped=CASE WHEN observation_previews.version_id=excluded.version_id THEN observation_previews.stopped ELSE 0 END
                """, ("$entity", entityId.Value.ToString()), ("$version", version.VersionId.Value.ToString()));
        return version.VersionId;
    }

    internal IReadOnlyList<VersionId> ObservationPreviewIds()
    {
        using var command = Command("SELECT version_id FROM observation_previews WHERE stopped=0 ORDER BY entity_id");
        using var reader = command.ExecuteReader();
        var result = new List<VersionId>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0))));
        return result;
    }

    internal (VersionSubmission Version, long Sequence)? PrepareObservationPreview(VersionId id)
    {
        var local = Find(id);
        if (local is not { State: SubmissionState.Draft, Version.Content: Observation }
            || References(local.Version).Any(reference => Find(reference.Target)?.State != SubmissionState.Saved)) return null;
        using var command = Command("""
            UPDATE observation_previews SET sequence=sequence+1 WHERE version_id=$id AND stopped=0 RETURNING sequence
            """, ("$id", id.Value.ToString()));
        return command.ExecuteScalar() is long sequence ? (local.Version, sequence) : null;
    }

    internal void StopObservationPreview(VersionId id) => Execute(
        "UPDATE observation_previews SET stopped=1 WHERE version_id=$id", ("$id", id.Value.ToString()));

    public VersionId Resolve(VersionId version)
    {
        var visited = new HashSet<VersionId>();
        while (Metadata("alias:" + version.Value) is { } alias)
        {
            if (!visited.Add(version)) throw new InvalidDataException("Version alias cycle.");
            version = new(Guid.Parse(alias));
        }
        return version;
    }

    public VersionId Reference(EntityId entityId)
    {
        var latest = Latest(entityId) ?? throw new InvalidOperationException("The entity is not registered locally.");
        Freeze(latest.Version.VersionId);
        return latest.Version.VersionId;
    }

    public void Complete(EntityId entityId) => Freeze((Latest(entityId) ?? throw new InvalidOperationException("Unknown local entity.")).Version.VersionId);
    public void Freeze(VersionId id) => Freeze(id, []);

    private void Freeze(VersionId id, HashSet<VersionId> visiting)
    {
        var local = Find(id);
        if (local is null || local.State != SubmissionState.Draft) return;
        if (!visiting.Add(id)) throw new InvalidOperationException("Draft references contain a cycle.");
        FreezeReferences(local.Version, visiting);
        visiting.Remove(id);
        Put(local.Version, SubmissionState.Frozen, local.Strategy, NextOrder());
        if (local.Version.Content is Observation) StopObservationPreview(id);
    }

    private void FreezeReferences(VersionSubmission version, HashSet<VersionId> visiting)
    {
        foreach (var reference in References(version))
        {
            if (reference.Target == version.VersionId) throw new InvalidOperationException("A draft cannot reference itself.");
            Freeze(reference.Target, visiting);
        }
    }

    internal IReadOnlyList<VersionReference> References(VersionSubmission version) => VersionRules.GetReferences(version,
        version.SchemaVersionId is { } schema ? Find(schema)?.Version.Content as EntitySchema : null);

    public void Remember(EntityVersion saved)
    {
        var current = Latest(saved.EntityId);
        Put(saved.Submission, SubmissionState.Saved, SubmissionStrategy.Immediate, null);
        if (current is null || current.State == SubmissionState.Saved) SetLatest(saved.EntityId, saved.VersionId);
    }

    internal void Confirm(VersionId id) => Execute("UPDATE local_versions SET state = 'Saved', rule = NULL, detail = NULL WHERE version_id = $id", ("$id", id.Value.ToString()));
    internal void Reject(VersionId id, string rule, string detail) => Execute("UPDATE local_versions SET state = 'Rejected', rule = $rule, detail = $detail WHERE version_id = $id",
        ("$id", id.Value.ToString()), ("$rule", rule), ("$detail", detail));

    public RejectedVersion? Rejection()
    {
        var versions = Versions();
        var rejected = versions.FirstOrDefault(version => version.State == SubmissionState.Rejected);
        if (rejected is null) return null;
        var dependencies = new HashSet<VersionId> { rejected.Version.VersionId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var version in versions.Where(version => version.State != SubmissionState.Saved))
                if (References(version.Version).Any(reference => dependencies.Contains(reference.Target)))
                    changed |= dependencies.Add(version.Version.VersionId);
        }
        return new(rejected, versions.Where(version => version != rejected && dependencies.Contains(version.Version.VersionId)).ToArray());
    }

    public void DiscardRejected()
    {
        var rejection = Rejection() ?? throw new InvalidOperationException("There is no rejected version.");
        foreach (var local in rejection.Dependents.Reverse().Append(rejection.Version)) Remove(local.Version.VersionId);
    }

    public void AdoptSaved(EntityVersion saved)
    {
        var rejected = Rejection()?.Version ?? throw new InvalidOperationException("There is no rejected version.");
        var submitted = rejected.Version;
        if (rejected.Rule != "VERSION-6" || submitted.EntityId != saved.EntityId
            || !VersionRules.SameSubmission(submitted, saved.Submission with { VersionId = submitted.VersionId, PreviousVersionId = submitted.PreviousVersionId }))
            throw new InvalidOperationException("Only an equal saved version of the same entity can replace a VERSION-6 rejection.");
        ReplaceWithSaved(submitted.VersionId, saved);
    }

    internal void ReplaceWithSaved(VersionId old, EntityVersion saved)
    {
        Put(saved.Submission, SubmissionState.Saved, SubmissionStrategy.Immediate, null);
        Metadata("alias:" + old.Value, saved.VersionId.Value.ToString());
        RewriteReferences(old, saved.VersionId);
        Execute("UPDATE latest SET version_id = $new WHERE version_id = $old", ("$new", saved.VersionId.Value.ToString()), ("$old", old.Value.ToString()));
        Remove(old);
        Execute("UPDATE latest SET version_id = $new WHERE entity_id = $entity AND NOT EXISTS (SELECT 1 FROM local_versions v WHERE v.version_id = latest.version_id)",
            ("$new", saved.VersionId.Value.ToString()), ("$entity", saved.EntityId.Value.ToString()));
        if (Latest(saved.EntityId) is null) SetLatest(saved.EntityId, saved.VersionId);
    }

    // Only a definitive rejection permits rewriting frozen data. Business code supplies the new identity/content.
    public VersionId ReplaceRejected(EntityId entityId, VersionContent content, VersionId? schemaVersionId, VersionId? previousVersionId = null)
    {
        var rejected = Rejection()?.Version ?? throw new InvalidOperationException("There is no rejected version.");
        var wasLatest = Latest(entityId)?.Version.VersionId == rejected.Version.VersionId || entityId != rejected.Version.EntityId;
        var replacement = new VersionSubmission(entityId, VersionId.New(), previousVersionId, schemaVersionId, content);
        FreezeReferences(replacement, []);
        Put(replacement, SubmissionState.Frozen, rejected.Strategy, rejected.FreezeOrder);
        RewriteReferences(rejected.Version.VersionId, replacement.VersionId);
        Remove(rejected.Version.VersionId);
        if (wasLatest) SetLatest(entityId, replacement.VersionId);
        ReorderFrozen();
        return replacement.VersionId;
    }

    public void ReplaceReferenceAndResume(VersionId old, VersionId replacement)
    {
        if (Rejection() is null) throw new InvalidOperationException("Only rejected submissions can be rewritten.");
        Freeze(replacement);
        RewriteReferences(old, replacement);
        Execute("UPDATE local_versions SET state = 'Frozen', rule = NULL, detail = NULL WHERE state = 'Rejected'");
        ReorderFrozen();
    }

    internal VersionId? PendingSchemaPredecessor(LocalVersion registration) => Versions()
        .Where(version => version.Version.EntityId == registration.Version.EntityId && version.State == SubmissionState.Frozen
            && version.FreezeOrder < registration.FreezeOrder)
        .OrderBy(version => version.FreezeOrder).LastOrDefault()?.Version.VersionId;

    internal void SetRegistrationHead(EntityId entityId, VersionId? savedHead)
    {
        var pending = Versions().Where(version => version.Version.EntityId == entityId
            && version.State is SubmissionState.Frozen or SubmissionState.Draft)
            .OrderBy(version => version.FreezeOrder ?? long.MaxValue).LastOrDefault();
        if ((pending?.Version.VersionId ?? savedHead) is { } head) SetLatest(entityId, head);
    }

    internal void RebaseUnsent(VersionId id, VersionId? predecessor)
    {
        var local = Find(id)!;
        Put(local.Version with { PreviousVersionId = predecessor }, local.State, local.Strategy, local.FreezeOrder);
    }

    private void RewriteReferences(VersionId old, VersionId replacement)
    {
        foreach (var local in Versions().Where(version => version.State != SubmissionState.Saved && version.Version.VersionId != old))
        {
            var references = References(local.Version).Where(reference => reference.Target == old).ToArray();
            if (references.Length == 0) continue;
            var node = JsonNode.Parse(VersionJson.Submission(local.Version).GetRawText())!;
            foreach (var reference in references)
            {
                var path = reference.Position.Split('/').Skip(1).Select(part => part.Replace("~1", "/").Replace("~0", "~")).ToArray();
                var parent = node;
                foreach (var part in path[..^1]) parent = parent is JsonArray array ? array[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)]! : parent[part]!;
                if (parent is JsonArray elements) elements[int.Parse(path[^1], System.Globalization.CultureInfo.InvariantCulture)] = replacement.Value.ToString();
                else parent[path[^1]] = replacement.Value.ToString();
            }
            Put(VersionJson.ReadSubmission(JsonSerializer.SerializeToElement(node)), local.State, local.Strategy, local.FreezeOrder, local.Rule, local.Detail);
        }
    }

    private void ReorderFrozen()
    {
        var ordered = new HashSet<VersionId>();
        var visiting = new HashSet<VersionId>();
        foreach (var local in Versions().Where(local => local.State == SubmissionState.Frozen).OrderBy(local => local.FreezeOrder)) Visit(local);
        void Visit(LocalVersion local)
        {
            if (ordered.Contains(local.Version.VersionId)) return;
            if (!visiting.Add(local.Version.VersionId)) throw new InvalidOperationException("References contain a cycle.");
            foreach (var reference in References(local.Version))
                if (Find(reference.Target) is { State: SubmissionState.Frozen } dependency) Visit(dependency);
            visiting.Remove(local.Version.VersionId);
            ordered.Add(local.Version.VersionId);
            Put(local.Version, local.State, local.Strategy, NextOrder());
        }
    }

    private void Remove(VersionId id)
    {
        var local = Find(id);
        if (local is null) return;
        Execute("DELETE FROM local_versions WHERE version_id = $id", ("$id", id.Value.ToString()));
        Execute("DELETE FROM latest WHERE version_id = $id", ("$id", id.Value.ToString()));
        if (Latest(local.Version.EntityId) is null && local.Version.PreviousVersionId is { } previous && Find(previous) is not null)
            SetLatest(local.Version.EntityId, previous);
    }

    private long NextOrder()
    {
        Execute("UPDATE local_metadata SET value = CAST(value AS INTEGER) + 1 WHERE key = 'freeze_order'");
        return long.Parse(Metadata("freeze_order")!, System.Globalization.CultureInfo.InvariantCulture);
    }
    internal string? Metadata(string key)
    {
        using var command = Command("SELECT value FROM local_metadata WHERE key = $key", ("$key", key));
        return command.ExecuteScalar() as string;
    }
    internal void Metadata(string key, string value) => Execute("INSERT INTO local_metadata(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$key", key), ("$value", value));
    private void SetLatest(EntityId entity, VersionId version) => Execute("INSERT INTO latest(entity_id,version_id) VALUES($entity,$version) ON CONFLICT(entity_id) DO UPDATE SET version_id = excluded.version_id", ("$entity", entity.Value.ToString()), ("$version", version.Value.ToString()));
    private void Put(VersionSubmission version, SubmissionState state, SubmissionStrategy strategy, long? order, string? rule = null, string? detail = null) => Execute("""
        INSERT INTO local_versions(version_id,entity_id,payload,state,strategy,freeze_order,rule,detail)
        VALUES($id,$entity,$payload,$state,$strategy,$order,$rule,$detail)
        ON CONFLICT(version_id) DO UPDATE SET payload=excluded.payload,state=excluded.state,
            strategy=excluded.strategy,freeze_order=excluded.freeze_order,rule=excluded.rule,detail=excluded.detail
        """, ("$id", version.VersionId.Value.ToString()), ("$entity", version.EntityId.Value.ToString()),
        ("$payload", VersionJson.Submission(version).GetRawText()), ("$state", state.ToString()), ("$strategy", strategy.ToString()),
        ("$order", order), ("$rule", rule), ("$detail", detail));

    private List<LocalVersion> Read(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var versions = new List<LocalVersion>();
        while (reader.Read())
        {
            using var payload = JsonDocument.Parse(reader.GetString(0));
            versions.Add(new(VersionJson.ReadSubmission(payload.RootElement), Enum.Parse<SubmissionState>(reader.GetString(1)),
                Enum.Parse<SubmissionStrategy>(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return versions;
    }
    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }
    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }
}
