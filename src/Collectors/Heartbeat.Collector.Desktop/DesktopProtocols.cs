using System.Text.Json;
using Heartbeat.Hub;

namespace Heartbeat.Collector.Desktop;

internal static class DesktopProtocols
{
    public static readonly TrackDeclaration Application = new(
        "desktop.application.foreground", 1, "range");
    public static readonly TrackDeclaration Window = new(
        "desktop.window.foreground", 1, "range");
    public static readonly TrackDeclaration Away = new(
        "desktop.system.away", 1, "range");
    public static readonly TrackDeclaration Input = new(
        "desktop.input.event", 1, "point");
    public static readonly TrackDeclaration ObservationStatus = new(
        "desktop.observation.status", 1, "range");

    public static JsonElement ApplicationValue() => JsonSerializer.SerializeToElement(new { });

    public static JsonElement WindowValue(string title) =>
        JsonSerializer.SerializeToElement(new { window = new { title } });

    public static JsonElement AwayValue(DesktopAwayReason reason) =>
        JsonSerializer.SerializeToElement(new { reason = Snake(reason) });

    public static JsonElement InputValue(DesktopInputObservation input)
    {
        object value = input.Kind switch
        {
            DesktopInputKind.KeyDown => new
            {
                kind = "key_down",
                code_set = InputCodeSets.HeartbeatKeyPositionV1,
                code = input.Code,
            },
            DesktopInputKind.MouseButtonDown => new
            {
                kind = "mouse_button_down",
                button = input.Code,
            },
            DesktopInputKind.Scroll => new
            {
                kind = "scroll",
                delta_x = input.DeltaX,
                delta_y = input.DeltaY,
                unit = input.ScrollUnit switch
                {
                    global::Heartbeat.Collector.Desktop.ScrollUnit.Line => "line",
                    global::Heartbeat.Collector.Desktop.ScrollUnit.Point => "point",
                    _ => throw new ArgumentException("A recorded scroll event requires a native unit.", nameof(input)),
                },
            },
            _ => throw new ArgumentException("Only recorded input observations have a protocol value.", nameof(input)),
        };
        return JsonSerializer.SerializeToElement(value);
    }

    public static JsonElement StatusValue(CapabilityObservation status) =>
        JsonSerializer.SerializeToElement(new
        {
            capability = Snake(status.Capability), state = Snake(status.State), reason = status.Reason,
        });

    private static string Snake<T>(T value) where T : struct, Enum =>
        string.Concat(value.ToString().Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $"_{char.ToLowerInvariant(character)}" : char.ToLowerInvariant(character).ToString()));

}
