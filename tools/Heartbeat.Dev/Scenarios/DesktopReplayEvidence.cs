using System.Globalization;
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

    public static string Query(Guid owner, string target, string applicationId, DateTimeOffset since, DateTimeOffset until) => $"""
        select row_to_json(witness) from (
            select t.id as "TrackId", r.id as "RecordId", r.started_at as "StartedAt", r.ended_at as "EndedAt"
            from records r join tracks t on t.id = r.track_id
            join collectors c on c.id = t.collector_id join timelines l on l.id = c.timeline_id
            where l.owner_id = '{owner:D}'::uuid
              and c.key = 'heartbeat.collector.desktop.macos' and c.target = '{target.Replace("'", "''", StringComparison.Ordinal)}'
              and t.type = 'desktop.application.foreground' and t.version = 1
              and t.time_mode = 'range' and t.end_mode = 'explicit'
              and r.value->>'device_id' = c.target
              and r.value->'application'->>'platform' = 'macos'
              and r.value->'application'->>'id_kind' = 'bundle_id'
              and r.value->'application'->>'id' = '{applicationId.Replace("'", "''", StringComparison.Ordinal)}'
              and r.started_at >= timestamptz '{since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}'
              and r.ended_at <= timestamptz '{until.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}'
              and r.ended_at >= r.started_at + interval '2 seconds'
            order by r.started_at, r.id limit 1
        ) witness;
        """;
}
