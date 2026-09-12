using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Recording.Protocols;

public static class RecordProtocols
{
    public static RecordProtocol ForegroundApplication { get; } = new(
        "desktop.application.foreground",
        1,
        TimeMode.Range,
        EndMode.Explicit,
        supportsExtension: true,
        ValidateForegroundApplication);

    public static RecordProtocol? Find(string type, int version) =>
        type == ForegroundApplication.Type && version == ForegroundApplication.Version
            ? ForegroundApplication
            : null;

    private static void ValidateForegroundApplication(JsonElement value)
    {
        if (value.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException("A foreground application value must be an object.", nameof(value));
        }

        ForegroundApplicationValue observation;
        try
        {
            observation = value.Deserialize<ForegroundApplicationValue>()
                ?? throw new JsonException("An observation is required.");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The foreground application value is invalid.", nameof(value), exception);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(observation.DeviceId, nameof(value));
        if (observation.Application is null)
        {
            throw new ArgumentException("An application reference is required.", nameof(value));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(observation.Application.Platform, nameof(value));
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.Application.IdKind, nameof(value));
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.Application.Id, nameof(value));
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ForegroundApplicationValue(
        [property: JsonPropertyName("device_id")] string? DeviceId,
        [property: JsonPropertyName("application")] ApplicationReference? Application);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ApplicationReference(
        [property: JsonPropertyName("platform")] string? Platform,
        [property: JsonPropertyName("id_kind")] string? IdKind,
        [property: JsonPropertyName("id")] string? Id);
}
