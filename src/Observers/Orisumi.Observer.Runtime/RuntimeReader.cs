using System.Net;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;
using Orisumi.Observer.Runtime.Store;
using System.Globalization;

namespace Orisumi.Observer.Runtime;

internal sealed class RuntimeReader(HttpClient http)
{
    private sealed record Item(VersionSubmission Version, string State);
    private sealed record Snapshot(Guid InstanceId, string Query, Item[] Items, bool Complete, string? Error, DateTimeOffset Created);
    private readonly Dictionary<string, Snapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Item[]> _lastReads = new(StringComparer.Ordinal);

    public async Task<JsonElement> ReadAsync(ManagedObserver observer, JsonElement parameters, CancellationToken token)
    {
        var from = VersionJson.ParseTime(parameters.GetProperty("from").GetString()!);
        var to = VersionJson.ParseTime(parameters.GetProperty("to").GetString()!);
        if (from >= to) throw new ArgumentException("to must be later than from.");
        var resource = parameters.TryGetProperty("resourceName", out var resourceValue) ? resourceValue.GetString() : null;
        var limit = parameters.TryGetProperty("limit", out var size) ? size.GetInt32() : 200;
        if (limit is < 1 or > 1000) throw new ArgumentException("limit must be 1 through 1000.");
        var query = observer.Registration.InstanceId + "|" + from.ToString("O", CultureInfo.InvariantCulture) + "|" + to.ToString("O", CultureInfo.InvariantCulture) + "|" + resource;
        foreach (var key in _snapshots.Where(pair => DateTimeOffset.UtcNow - pair.Value.Created > TimeSpan.FromMinutes(2)).Select(pair => pair.Key).ToArray()) _snapshots.Remove(key);
        Snapshot snapshot;
        string snapshotId;
        var offset = 0;
        if (parameters.TryGetProperty("cursor", out var cursor) && cursor.ValueKind != JsonValueKind.Null)
        {
            var parts = cursor.GetString()!.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out offset) || offset < 0
                || !_snapshots.TryGetValue(parts[0], out snapshot!) || snapshot.InstanceId != observer.Registration.InstanceId || snapshot.Query != query)
                throw new InvalidOperationException("Read cursor expired; start the query again.");
            snapshotId = parts[0];
        }
        else
        {
            snapshotId = Guid.NewGuid().ToString("N");
            snapshot = await BuildAsync(observer, query, from, to, resource, token);
            if (_snapshots.Count >= 32) _snapshots.Remove(_snapshots.MinBy(pair => pair.Value.Created).Key);
            _snapshots[snapshotId] = snapshot;
        }
        var page = snapshot.Items.Skip(offset).Take(limit).Select(item => new { version = VersionJson.Submission(item.Version), state = item.State }).ToArray();
        var next = offset + page.Length < snapshot.Items.Length ? snapshotId + ":" + (offset + page.Length).ToString(CultureInfo.InvariantCulture) : null;
        return JsonLines.Json(new { observations = page, next, snapshot.Complete, snapshot.Error });
    }

    private async Task<Snapshot> BuildAsync(ManagedObserver observer, string query, DateTimeOffset from, DateTimeOffset to,
        string? resource, CancellationToken token)
    {
        var (local, currentVersions) = LocalSnapshot(observer.Store);
        var remote = new Dictionary<EntityId, Item>();
        var cache = new Dictionary<VersionId, VersionSubmission?>();
        var complete = true;
        string? error = null;
        try
        {
            string? after = null;
            do
            {
                var path = "observations?limit=1000&from=" + Uri.EscapeDataString(VersionTime.Format(from)) + "&to=" + Uri.EscapeDataString(VersionTime.Format(to));
                if (resource is not null) path += "&resourceName=" + Uri.EscapeDataString(resource);
                if (after is not null) path += "&after=" + Uri.EscapeDataString(after);
                using var response = await http.GetAsync(path, token);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                foreach (var value in json.RootElement.GetProperty("versions").EnumerateArray())
                {
                    var version = VersionJson.ReadSaved(value).Submission;
                    if (await BelongsAsync(version)) remote[version.EntityId] = new(version, "saved");
                }
                after = json.RootElement.GetProperty("next").GetString();
            } while (after is not null);
            // Snapshot local again: sending may have confirmed a version after the list was read.
            (local, currentVersions) = LocalSnapshot(observer.Store);
            foreach (var item in currentVersions)
            {
                if (item.Version.Content is not Observation observation || !Overlaps(observation, from, to)) continue;
                if (!await BelongsAsync(item.Version) || !await MatchesResourceAsync(observation, resource)) continue;
                if (remote.TryGetValue(item.Version.EntityId, out var saved))
                {
                    if (item.Version.VersionId == saved.Version.VersionId) continue;
                    if (item.State != SubmissionState.Saved && DescendsFrom(item.Version, saved.Version.VersionId, local))
                        remote[item.Version.EntityId] = new(item.Version, State(item.State));
                    continue;
                }
                using var response = await http.GetAsync("entities/" + item.Version.EntityId.Value, token);
                if (response.StatusCode == HttpStatusCode.Gone)
                {
                    observer.Store.Execute(transaction => transaction.RememberDeleted(item.Version.EntityId));
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    if (item.State != SubmissionState.Saved) remote[item.Version.EntityId] = new(item.Version, State(item.State));
                    continue;
                }
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var current = VersionJson.ReadSaved(json.RootElement).Submission;
                var selected = item.State != SubmissionState.Saved && DescendsFrom(item.Version, current.VersionId, local)
                    ? new Item(item.Version, State(item.State)) : new Item(current, "saved");
                if (selected.Version.Content is Observation selectedObservation && Overlaps(selectedObservation, from, to)
                    && await BelongsAsync(selected.Version) && await MatchesResourceAsync(selectedObservation, resource)) remote[item.Version.EntityId] = selected;
            }
            if (_lastReads.Count >= 32 && !_lastReads.ContainsKey(query)) _lastReads.Remove(_lastReads.Keys.First());
            _lastReads[query] = remote.Values.ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException || exception is OperationCanceledException && !token.IsCancellationRequested)
        {
            complete = false;
            error = exception.Message;
            remote = (_lastReads.GetValueOrDefault(query) ?? []).ToDictionary(item => item.Version.EntityId);
            var deleted = observer.Store.Execute(transaction => local.Values.Select(item => item.Version.EntityId).Concat(remote.Keys)
                .Where(transaction.IsKnownDeleted).ToHashSet());
            foreach (var id in deleted) remote.Remove(id);
            foreach (var item in currentVersions)
            {
                if (deleted.Contains(item.Version.EntityId)) continue;
                if (item.Version.Content is not Observation observation || !Overlaps(observation, from, to)) continue;
                if (local.GetValueOrDefault(observation.ObserverVersionId)?.Version.EntityId.Value != observer.Registration.InstanceId) continue;
                if (resource is not null && (local.GetValueOrDefault(observation.ContentVersionId)?.Version.SchemaVersionId is not { } schema
                    || local.GetValueOrDefault(schema)?.Version.Content is not EntitySchema entitySchema || entitySchema.ResourceName != resource)) continue;
                if (item.State != SubmissionState.Saved || !remote.ContainsKey(item.Version.EntityId)) remote[item.Version.EntityId] = new(item.Version, State(item.State));
            }
        }
        var items = remote.Values.OrderByDescending(item => ((Observation)item.Version.Content).StartAt ?? ((Observation)item.Version.Content).EndAt)
            .ThenBy(item => item.Version.EntityId.Value).ToArray();
        return new(observer.Registration.InstanceId, query, items, complete, error, DateTimeOffset.UtcNow);

        async Task<VersionSubmission?> LookupAsync(VersionId id)
        {
            if (cache.TryGetValue(id, out var found)) return found;
            if (local.TryGetValue(id, out var value)) return cache[id] = value.Version;
            return cache[id] = (await observer.Store.ReadVersionAsync(id, token))?.Submission;
        }
        async Task<bool> BelongsAsync(VersionSubmission version) => version.Content is Observation observation
            && (await LookupAsync(observation.ObserverVersionId))?.EntityId.Value == observer.Registration.InstanceId;
        async Task<bool> MatchesResourceAsync(Observation observation, string? expected)
        {
            if (expected is null) return true;
            var content = await LookupAsync(observation.ContentVersionId);
            return content?.SchemaVersionId is { } schema && (await LookupAsync(schema))?.Content is EntitySchema type && type.ResourceName == expected;
        }
    }

    private static (Dictionary<VersionId, LocalVersion> Versions, LocalVersion[] Current) LocalSnapshot(ObserverStore runtimeStore) =>
        runtimeStore.Execute(transaction => (transaction.Versions().ToDictionary(item => item.Version.VersionId),
            transaction.CurrentVersions().Where(item => !transaction.IsKnownDeleted(item.Version.EntityId)).ToArray()));

    private static bool DescendsFrom(VersionSubmission version, VersionId ancestor, Dictionary<VersionId, LocalVersion> local)
    {
        var current = version;
        var visited = new HashSet<VersionId>();
        while (visited.Add(current.VersionId))
        {
            if (current.VersionId == ancestor || current.PreviousVersionId == ancestor) return true;
            if (current.PreviousVersionId is not { } predecessor || !local.TryGetValue(predecessor, out var previous)) return false;
            current = previous.Version;
        }
        return false;
    }
    private static bool Overlaps(Observation observation, DateTimeOffset from, DateTimeOffset to) =>
        (observation.StartAt is null || observation.StartAt < to) && (observation.EndAt is null || observation.EndAt > from
            || observation.StartAt == observation.EndAt && observation.EndAt == from);
    private static string State(SubmissionState state) => state == SubmissionState.Frozen ? "pending" : state.ToString().ToLowerInvariant();

    public static async Task<JsonElement> VersionsAsync(ManagedObserver observer, JsonElement parameters, CancellationToken token)
    {
        var ids = parameters.GetProperty("versionIds").EnumerateArray().Select(item => new VersionId(item.GetGuid())).ToArray();
        if (ids.Length > 256) throw new ArgumentException("At most 256 version ids are allowed.");
        var versions = new List<JsonElement>();
        var missing = new List<Guid>();
        string? error = null;
        foreach (var id in ids.Distinct())
        {
            if (observer.Store.Execute(transaction => transaction.IsKnownMissing(id)
                || transaction.Find(id) is { } known && transaction.IsKnownDeleted(known.Version.EntityId)))
            { missing.Add(id.Value); continue; }
            var local = observer.Store.Execute(transaction => transaction.Find(id));
            VersionSubmission? version;
            if (local is { State: not SubmissionState.Saved }) version = local.Version;
            else
            {
                try
                {
                    version = (await observer.Store.ReadVersionAsync(id, token))?.Submission;
                    if (version is null && local?.State == SubmissionState.Saved)
                        observer.Store.Execute(transaction => transaction.RememberMissing(id));
                }
                catch (Exception exception) when (exception is HttpRequestException || exception is OperationCanceledException && !token.IsCancellationRequested)
                { error = exception.Message; version = local?.Version; }
            }
            if (version is null) missing.Add(id.Value); else versions.Add(VersionJson.Submission(version));
        }
        return JsonLines.Json(new { versions, missing, error });
    }
}
