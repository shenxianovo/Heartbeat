using System.Net;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;
using System.Globalization;

namespace Orisumi.Observer.Runtime;

internal sealed class RuntimeReader(HttpClient http, Func<bool>? canRead = null)
{
    private sealed record Snapshot(Guid InstanceId, string Query, VersionSubmission[] Items, DateTimeOffset Created);
    private readonly Dictionary<string, Snapshot> _snapshots = new(StringComparer.Ordinal);

    internal async Task<JsonElement> ContentAsync(JsonElement parameters, CancellationToken token)
    {
        var digest = parameters.GetProperty("digest").GetString()!;
        if (!BinaryValue.ValidDigest(digest)) throw new ArgumentException("Invalid content digest.");
        EnsureConnected();
        using var response = await http.GetAsync("contents/" + digest, token);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new KeyNotFoundException("Content is not available.");
        response.EnsureSuccessStatusCode();
        return JsonLines.Json(new { data = await response.Content.ReadAsByteArrayAsync(token) });
    }

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
        var page = snapshot.Items.Skip(offset).Take(limit).Select(VersionJson.Submission).ToArray();
        var next = offset + page.Length < snapshot.Items.Length ? snapshotId + ":" + (offset + page.Length).ToString(CultureInfo.InvariantCulture) : null;
        return JsonLines.Json(new { observations = page, next });
    }

    private async Task<Snapshot> BuildAsync(ManagedObserver observer, string query, DateTimeOffset from, DateTimeOffset to,
        string? resource, CancellationToken token)
    {
        var items = new List<VersionSubmission>();
        var cache = new Dictionary<VersionId, VersionSubmission?>();
        string? after = null;
        do
        {
            var path = "observations?limit=1000&from=" + Uri.EscapeDataString(VersionTime.Format(from)) + "&to=" + Uri.EscapeDataString(VersionTime.Format(to));
            if (resource is not null) path += "&resourceName=" + Uri.EscapeDataString(resource);
            if (after is not null) path += "&after=" + Uri.EscapeDataString(after);
            EnsureConnected();
            using var response = await http.GetAsync(path, token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            foreach (var value in json.RootElement.GetProperty("versions").EnumerateArray())
            {
                var version = VersionJson.ReadSaved(value).Submission;
                if (version.Content is Observation observation && (await LookupAsync(observation.ObserverVersionId))?.EntityId.Value == observer.Registration.InstanceId)
                    items.Add(version);
            }
            after = json.RootElement.GetProperty("next").GetString();
        } while (after is not null);
        var ordered = items.OrderByDescending(item => ((Observation)item.Content).StartAt ?? ((Observation)item.Content).EndAt)
            .ThenBy(item => item.EntityId.Value).ToArray();
        return new(observer.Registration.InstanceId, query, ordered, DateTimeOffset.UtcNow);

        async Task<VersionSubmission?> LookupAsync(VersionId id)
        {
            if (cache.TryGetValue(id, out var found)) return found;
            var response = await observer.Session.ReadVersionsAsync([id], token);
            return cache[id] = response.GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSubmission).SingleOrDefault();
        }
    }

    public static Task<JsonElement> VersionsAsync(ManagedObserver observer, JsonElement parameters, CancellationToken token) =>
        observer.Session.ReadVersionsAsync(parameters.GetProperty("versionIds").EnumerateArray().Select(item => new VersionId(item.GetGuid())), token);

    private void EnsureConnected()
    {
        if (canRead?.Invoke() == false) throw new HttpRequestException("Runtime is not connected.");
    }
}
