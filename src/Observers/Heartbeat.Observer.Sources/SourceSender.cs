using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Heartbeat.Observers.Sources;

public sealed class SourceSender(SourceStore store, HttpClient client)
{
    public async Task<int> SynchronizeAsync(CancellationToken cancellationToken)
    {
        var confirmed = 0;
        foreach (var pending in store.Pending())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var present = true;
                foreach (var entity in pending.Entities)
                    present &= await CheckOrSendAsync(entity, cancellationToken);
                if (present) { store.Confirm(pending.Sequence); confirmed++; }
                else store.RecordError(pending.Sequence, null);
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException
                || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                store.RecordError(pending.Sequence, error.Message);
                if (error is OperationCanceledException or HttpRequestException { StatusCode: null }) break;
            }
        }
        return confirmed;
    }

    private async Task<bool> CheckOrSendAsync(SourceSubmission entity, CancellationToken cancellationToken)
    {
        using var read = await client.GetAsync($"entities/{entity.Id}", cancellationToken);
        if (read.StatusCode == HttpStatusCode.OK)
        {
            var value = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            SourceJson.Validate(value);
            if (!Matches(entity, value)) throw new InvalidDataException($"Entity {entity.Id} differs from durable source evidence; retained locally.");
            return true;
        }
        if (read.StatusCode != HttpStatusCode.NotFound) throw Failure(read.StatusCode);
        using var save = await client.PutAsJsonAsync($"entities/{entity.Resource}/{entity.Id}", entity.Body, cancellationToken);
        if (save.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.NoContent)) throw Failure(save.StatusCode);
        return false; // A later GET must confirm every entity before the outbox record is removed.
    }

    public static bool Matches(SourceSubmission expected, JsonElement actual) =>
        actual.ValueKind == JsonValueKind.Object
        && actual.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.String && category.GetString() == expected.Category
        && actual.TryGetProperty("references", out var references)
        && actual.TryGetProperty("properties", out var properties)
        && JsonElement.DeepEquals(references, expected.Body.GetProperty("references"))
        && PropertiesMatch(expected.Category, expected.Body.GetProperty("properties"), properties)
        && (expected.SchemaId is null || actual.TryGetProperty("schemaId", out var schema) && schema.ValueKind == JsonValueKind.String && schema.TryGetGuid(out var id) && id == expected.SchemaId);

    private static bool PropertiesMatch(string category, JsonElement expected, JsonElement actual)
    {
        if (category != "entity_schema") return JsonElement.DeepEquals(expected, actual);
        // A schema's display name can change without changing its semantic identity.
        if (actual.ValueKind != JsonValueKind.Object || !actual.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())) return false;
        var expectedIdentity = expected.EnumerateObject().Where(p => p.Name != "name").ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        var actualIdentity = actual.EnumerateObject().Where(p => p.Name != "name").ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        return expectedIdentity.Count == actualIdentity.Count && expectedIdentity.All(p => actualIdentity.TryGetValue(p.Key, out var value) && JsonElement.DeepEquals(p.Value, value));
    }

    private static HttpRequestException Failure(HttpStatusCode status) =>
        new($"Source entity request returned HTTP {(int)status}; durable evidence retained.", null, status);
}
