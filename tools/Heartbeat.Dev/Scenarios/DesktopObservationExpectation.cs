using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed record CollectionMatches(int Total, int Owner, int Collector, int Target, int Track,
    int Payload, int Application, int TimeWindow, int Duration, DesktopReplayEvidence? Witness);

// These are acceptance conditions from the desktop recording contract, not production constants.
internal sealed record DesktopObservationExpectation(Guid Owner, string Target, string ApplicationId, DateTimeOffset Since)
{
    public CollectionMatches Inspect(ScenarioRecord[] records, DateTimeOffset until)
    {
        var remaining = records;
        int Match(Func<ScenarioRecord, bool> predicate)
        {
            remaining = remaining.Where(predicate).ToArray();
            return remaining.Length;
        }
        var owner = Match(item => item.OwnerId == Owner);
        var collector = Match(item => item.CollectorKey == "heartbeat.collector.desktop.macos");
        var target = Match(item => item.Target == Target);
        var track = Match(item => item.Track is { Type: "desktop.application.foreground", Version: 1, TimeMode: "range", EndMode: "explicit" });
        var payload = Match(item => HasDesktopPayload(item.Record.Value));
        var application = Match(item => item.Record.Value.GetProperty("application").GetProperty("id").GetString() == ApplicationId);
        var timeWindow = Match(item => item.Record.StartedAt >= Since && item.Record.EndedAt <= until);
        var duration = Match(item => item.Record.EndedAt - item.Record.StartedAt >= TimeSpan.FromSeconds(2));
        var witness = remaining.OrderBy(item => item.Record.StartedAt).ThenBy(item => item.Record.Id).FirstOrDefault();
        return new(records.Length, owner, collector, target, track, payload, application, timeWindow, duration,
            witness is null ? null : DesktopReplayEvidence.From(witness));
    }

    private bool HasDesktopPayload(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
        && HasString(value, "device_id", Target)
        && value.TryGetProperty("application", out var app) && app.ValueKind == JsonValueKind.Object
        && HasString(app, "platform", "macos") && HasString(app, "id_kind", "bundle_id")
        && app.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String;

    private static bool HasString(JsonElement value, string property, string expected) =>
        value.TryGetProperty(property, out var actual) && actual.ValueKind == JsonValueKind.String && actual.GetString() == expected;
}
