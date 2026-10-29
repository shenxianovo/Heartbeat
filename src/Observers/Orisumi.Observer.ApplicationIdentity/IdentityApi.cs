using System.Net;
using System.Text;
using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Observers.ApplicationIdentity;

internal sealed class IdentityRejection(string? rule, string message) : Exception(message)
{
    public string? Rule { get; } = rule;
}

internal sealed class IdentityApi(HttpClient http, IdentityState state)
{
    public async Task<EntityVersion?> ReadAsync(EntityId id, CancellationToken token)
    {
        using var response = await http.GetAsync($"entities/{id.Value}", token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;
        response.EnsureSuccessStatusCode();
        return VersionJson.ReadSaved(await ReadJsonAsync(response, token));
    }

    public async Task<List<EntityVersion>> ListAsync(string resource, CancellationToken token)
    {
        var results = new List<EntityVersion>();
        string? after = null;
        do
        {
            using var response = await http.GetAsync($"entities?resourceName={resource}&limit=1000" + Cursor(after), token);
            if (response.StatusCode == HttpStatusCode.NotFound && after is null) return [];
            response.EnsureSuccessStatusCode();
            var page = await ReadJsonAsync(response, token);
            results.AddRange(page.GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSaved));
            after = page.GetProperty("next").GetString();
        } while (after is not null);
        return results;
    }

    public async Task<List<EntityVersion>> PeriodsAsync(EntityId application, CancellationToken token)
    {
        var results = new List<EntityVersion>();
        var schemas = new Dictionary<VersionId, bool>();
        string? after = null;
        do
        {
            using var response = await http.GetAsync($"entities/{application.Value}/referrers?versions=current&limit=100" + Cursor(after), token);
            response.EnsureSuccessStatusCode();
            var page = await ReadJsonAsync(response, token);
            foreach (var item in page.GetProperty("referrers").EnumerateArray())
            {
                var version = VersionJson.ReadSaved(item.GetProperty("version"));
                if (version.SchemaVersionId is not { } schemaId) continue;
                if (!schemas.TryGetValue(schemaId, out var isPeriod))
                {
                    using var schemaResponse = await http.GetAsync($"versions/{schemaId.Value}", token);
                    if (schemaResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) continue;
                    schemaResponse.EnsureSuccessStatusCode();
                    isPeriod = VersionJson.ReadSaved(await ReadJsonAsync(schemaResponse, token)).EntityId == IdentityData.PeriodSchemaId;
                    schemas[schemaId] = isPeriod;
                }
                if (isPeriod) results.Add(version);
                if (results.Count == 100) return results;
            }
            after = page.GetProperty("next").GetString();
        } while (after is not null);
        return results;
    }

    public async Task<EntityVersion> EnsureAsync(EntityId id, VersionId? schema, VersionContent content, CancellationToken token)
    {
        var current = await ReadAsync(id, token);
        if (current is not null && JsonElement.DeepEquals(VersionJson.Content(current.Content), VersionJson.Content(content))) return current;
        await SubmitAsync([new(id, VersionId.New(), current?.VersionId, schema, content)], token);
        return (await ReadAsync(id, token))!;
    }

    public Task SubmitAsync(IEnumerable<VersionSubmission> versions, CancellationToken token)
    {
        state.SavePending(JsonSerializer.Serialize(new { versions = versions.Select(VersionJson.Submission) }));
        return ResumeAsync(token);
    }

    public async Task ResumeAsync(CancellationToken token)
    {
        if (state.Pending is not { } body) return;
        using var response = await http.PostAsync("versions", new StringContent(body, Encoding.UTF8, "application/json"), token);
        if (response.IsSuccessStatusCode) { state.ClearPending(); return; }
        if ((int)response.StatusCode >= 500) { response.EnsureSuccessStatusCode(); return; }
        var problem = await ReadJsonAsync(response, token);
        state.ClearPending();
        throw new IdentityRejection(problem.TryGetProperty("rule", out var rule) ? rule.GetString() : null,
            problem.TryGetProperty("detail", out var detail) ? detail.GetString()! : $"HTTP {(int)response.StatusCode}");
    }

    private static string Cursor(string? after) => after is null ? "" : "&after=" + Uri.EscapeDataString(after);
    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken token) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(token));
}
