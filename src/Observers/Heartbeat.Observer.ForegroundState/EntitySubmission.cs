using System.Text.Json;

namespace Heartbeat.Observers.ForegroundState;

internal sealed record EntitySubmission(Guid Id, string Category, string SavePath, JsonElement Body)
{
    internal bool Matches(JsonElement entity) =>
        entity.ValueKind == JsonValueKind.Object && entity.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.String && category.GetString() == Category
        && entity.TryGetProperty("references", out var references)
        && entity.TryGetProperty("properties", out var properties)
        && JsonElement.DeepEquals(references, Body.GetProperty("references"))
        && JsonElement.DeepEquals(properties, Body.GetProperty("properties"));
}
