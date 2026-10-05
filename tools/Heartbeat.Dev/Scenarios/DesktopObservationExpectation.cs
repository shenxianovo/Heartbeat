using Heartbeat.Hub;

namespace Heartbeat.Dev;

internal sealed record CollectionMatches(int Total, int Owner, int Collector, int Target, int Track,
    int Payload, int Application, int TimeWindow, int Duration, DesktopReplayEvidence? Witness);

// These are acceptance conditions from the desktop recording contract, not production constants.
internal sealed record DesktopObservationExpectation(Guid Owner, string Target, string ApplicationId, DateTimeOffset Since,
    string CollectorKey = "heartbeat.collector.desktop.macos")
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
        var collector = Match(item => item.CollectorKey == CollectorKey);
        var target = Match(item => item.Target == Target);
        var track = Match(item => item.Track is { Type: "desktop.application.foreground", Version: 1, TimeMode: "range" });
        var payload = Match(item => HasDesktopPayload(item.Record));
        var application = Match(item => item.Record.Objects.Any(reference => reference.Role == "application" && reference.Key == ApplicationId));
        var timeWindow = Match(item => item.Record.StartedAt >= Since && item.Record.EndedAt <= until);
        var duration = Match(item => item.Record.EndedAt - item.Record.StartedAt >= TimeSpan.FromSeconds(2));
        var witness = remaining.OrderBy(item => item.Record.StartedAt).ThenBy(item => item.Record.Id).FirstOrDefault();
        return new(records.Length, owner, collector, target, track, payload, application, timeWindow, duration,
            witness is null ? null : DesktopReplayEvidence.From(witness));
    }

    private bool HasDesktopPayload(RecordSnapshot record) =>
        record.Objects.Any(item => item.Role == "device" && item.Namespace == "device" && item.Key == Target)
        && record.Objects.Any(item => item.Role == "application" && item.Namespace == "app.macos.bundle_id");
}
