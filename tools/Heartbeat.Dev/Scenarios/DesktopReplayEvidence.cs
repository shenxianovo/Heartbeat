using System.Text.Json.Serialization;

namespace Heartbeat.Dev;

internal sealed record DesktopReplayEvidence(
    [property: JsonRequired] Guid TrackId,
    [property: JsonRequired] Guid RecordId,
    [property: JsonRequired] DateTimeOffset StartedAt,
    [property: JsonRequired] DateTimeOffset EndedAt)
{
    // Export only the controlled application's identity/times, never other native payloads.
    public void RequireContinuationOf(DesktopReplayEvidence previous)
    {
        if (TrackId != previous.TrackId || RecordId != previous.RecordId
            || StartedAt != previous.StartedAt || EndedAt < previous.EndedAt)
            throw new InvalidOperationException("The controlled Record identity or confirmed interval changed during delivery.");
    }

    public static DesktopReplayEvidence From(ScenarioRecord item) =>
        new(item.TrackId, item.Record.Id, item.Record.StartedAt!.Value, item.Record.EndedAt!.Value);
}

internal sealed record DesktopReplayBatch(ScenarioRecord[] Records, DesktopReplayEvidence Witness)
{
    public static DesktopReplayBatch From(ScenarioRecord[] records, Guid witnessId) =>
        new(records, DesktopReplayEvidence.From(records.Single(item => item.Record.Id == witnessId)));
}
