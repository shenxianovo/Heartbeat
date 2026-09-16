using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Hub;
using Heartbeat.Collector.Desktop.Mac.Native;

namespace Heartbeat.Collector.Desktop.Mac;

internal static class DesktopProtocols
{
    public const string CollectorKey = "heartbeat.collector.desktop.macos";

    public static readonly TrackDeclaration Application = new(
        "desktop.application.foreground", 1, "range", "explicit");
    public static readonly TrackDeclaration Window = new(
        "desktop.window.foreground", 1, "range", "explicit");
    public static readonly TrackDeclaration Away = new(
        "desktop.system.away", 1, "range", "explicit");
    public static readonly TrackDeclaration Input = new(
        "desktop.input.event", 1, "point", null);
    public static readonly TrackDeclaration ObservationStatus = new(
        "desktop.observation.status", 1, "range", "explicit");

    public static JsonElement ApplicationValue(string deviceId, ForegroundApplication application) =>
        JsonSerializer.SerializeToElement(new ApplicationRecordValue(
            deviceId,
            new ApplicationValueReference(
                application.Platform,
                application.IdKind,
                application.Id,
                application.DisplayName)));

    public static JsonElement WindowValue(string deviceId, string title) =>
        JsonSerializer.SerializeToElement(new WindowRecordValue(deviceId, new WindowValueReference(title)));

    public static JsonElement AwayValue(string deviceId, MacAwayReason reason) =>
        JsonSerializer.SerializeToElement(new AwayValueRecord(deviceId, Snake(reason)));

    public static JsonElement InputValue(string deviceId, DesktopInputObservation input)
    {
        object value = input.Kind switch
        {
            DesktopInputKind.KeyDown => new
            {
                device_id = deviceId,
                kind = "key_down",
                code_set = InputCodeSets.HeartbeatKeyPositionV1,
                code = input.Code,
            },
            DesktopInputKind.MouseButtonDown => new
            {
                device_id = deviceId,
                kind = "mouse_button_down",
                button = input.Code,
            },
            DesktopInputKind.Scroll => new
            {
                device_id = deviceId,
                kind = "scroll",
                delta_x = input.DeltaX,
                delta_y = input.DeltaY,
                unit = input.ScrollUnit switch
                {
                    global::Heartbeat.Collector.Desktop.Mac.ScrollUnit.Line => "line",
                    global::Heartbeat.Collector.Desktop.Mac.ScrollUnit.Point => "point",
                    _ => throw new ArgumentException("A recorded scroll event requires a native unit.", nameof(input)),
                },
            },
            _ => throw new ArgumentException("Only recorded input observations have a protocol value.", nameof(input)),
        };
        return JsonSerializer.SerializeToElement(value);
    }

    public static JsonElement StatusValue(string deviceId, CapabilityObservation status) =>
        JsonSerializer.SerializeToElement(new StatusValueRecord(
            deviceId,
            Snake(status.Capability),
            Snake(status.State),
            status.Reason));

    private static string Snake<T>(T value) where T : struct, Enum =>
        string.Concat(value.ToString().Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $"_{char.ToLowerInvariant(character)}" : char.ToLowerInvariant(character).ToString()));

    private sealed record ApplicationRecordValue(
        [property: JsonPropertyName("device_id")] string DeviceId,
        [property: JsonPropertyName("application")] ApplicationValueReference Application);

    private sealed record ApplicationValueReference(
        [property: JsonPropertyName("platform")] string Platform,
        [property: JsonPropertyName("id_kind")] string IdKind,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("display_name")] string? DisplayName);

    private sealed record WindowRecordValue(
        [property: JsonPropertyName("device_id")] string DeviceId,
        [property: JsonPropertyName("window")] WindowValueReference Window);

    private sealed record WindowValueReference([property: JsonPropertyName("title")] string Title);

    private sealed record AwayValueRecord(
        [property: JsonPropertyName("device_id")] string DeviceId,
        [property: JsonPropertyName("reason")] string Reason);
    private sealed record StatusValueRecord(
        [property: JsonPropertyName("device_id")] string DeviceId,
        [property: JsonPropertyName("capability")] string Capability,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("reason")] string? Reason);
}
