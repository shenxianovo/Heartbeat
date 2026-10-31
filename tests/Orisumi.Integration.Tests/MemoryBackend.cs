using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Integration.Tests;

internal sealed record StoredVersion(VersionSubmission Version, DateTimeOffset RecordedAt);

/// <summary>
/// A backend stand-in for Observer tests: it saves every submission as given and reads it back,
/// without Core validation. With an inner handler, such as the real API, it forwards every request
/// and records the versions the inner backend saved.
/// </summary>
internal sealed class MemoryBackend(HttpMessageHandler? inner = null) : HttpMessageHandler
{
    private readonly HttpMessageInvoker? _inner = inner is null ? null : new(inner, disposeHandler: false);
    private readonly object _gate = new();
    internal List<StoredVersion> Versions { get; } = [];
    internal List<VersionSubmission[]> Posts { get; } = [];
    internal Dictionary<string, byte[]> Contents { get; } = new(StringComparer.Ordinal);
    internal bool Offline { get; set; }
    /// <summary>When set, a submission containing a version it matches is rejected with this rule and nothing is saved.</summary>
    internal Func<VersionSubmission, string?>? Reject { get; set; }

    internal HttpClient CreateClient() => new(this, disposeHandler: false) { BaseAddress = new("http://localhost/") };

    internal IReadOnlyList<StoredVersion> Snapshot()
    {
        lock (_gate) return Versions.ToArray();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Offline) throw new HttpRequestException("offline");
        if (_inner is not null) return await ForwardAsync(request, cancellationToken);
        var path = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
        if (request.Method == HttpMethod.Put && path is ["contents", var digest])
        {
            var data = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            lock (_gate) Contents[digest] = data;
            return new(HttpStatusCode.NoContent);
        }
        if (request.Method == HttpMethod.Post && path is ["versions"])
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var versions = json.RootElement.GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSubmission).ToArray();
            lock (_gate)
            {
                Posts.Add(versions);
                for (var index = 0; index < versions.Length; index++)
                    if (Reject?.Invoke(versions[index]) is { } rule)
                        return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { index, versionId = versions[index].VersionId.Value, rule, detail = "rejected by the test" }) };
                var now = DateTimeOffset.UtcNow;
                Versions.AddRange(versions.Select(version => new StoredVersion(version, now)));
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { versions = versions.Select(version => new { versionId = version.VersionId.Value, recordedAt = VersionTime.Format(now) }) }) };
            }
        }
        if (request.Method != HttpMethod.Get) return new(HttpStatusCode.MethodNotAllowed);
        lock (_gate)
        {
            return path switch
            {
                ["entities", var id] => Found(Versions.LastOrDefault(item => item.Version.EntityId.Value == Guid.Parse(id))),
                ["entities", var id, "versions"] => Json(new { versions = Versions.Where(item => item.Version.EntityId.Value == Guid.Parse(id)).Select(Saved) }),
                ["versions", var id] => Found(Versions.LastOrDefault(item => item.Version.VersionId.Value == Guid.Parse(id))),
                ["contents", var key] => Contents.TryGetValue(key, out var data) ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) } : new(HttpStatusCode.NotFound),
                _ => new(HttpStatusCode.NotFound),
            };
        }
    }

    private async Task<HttpResponseMessage> ForwardAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath.Trim('/') != "versions") return await _inner!.SendAsync(request, token);
        var body = await request.Content!.ReadAsStringAsync(token);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await _inner!.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) return response;
        using var json = JsonDocument.Parse(body);
        var versions = json.RootElement.GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSubmission).ToArray();
        lock (_gate) { Posts.Add(versions); Versions.AddRange(versions.Select(version => new StoredVersion(version, DateTimeOffset.UtcNow))); }
        return response;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner?.Dispose();
        base.Dispose(disposing);
    }

    private static HttpResponseMessage Found(StoredVersion? version) => version is null ? new(HttpStatusCode.NotFound) : Json(Saved(version));
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static JsonElement Saved(StoredVersion version) => VersionJson.Saved(new(version.Version, version.RecordedAt));
}
