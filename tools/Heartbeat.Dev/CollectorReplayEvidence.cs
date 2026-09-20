using System.Globalization;
using System.Text.Json.Serialization;

namespace Heartbeat.Dev;

internal sealed record CollectorReplayEvidence(
    [property: JsonRequired] Guid TrackId,
    [property: JsonRequired] Guid RecordId,
    [property: JsonRequired] DateTimeOffset StartedAt,
    [property: JsonRequired] DateTimeOffset EndedAt)
{
    // Export only the controlled application's identity/times, never other native payloads.
    public void RequireContinuationOf(CollectorReplayEvidence previous)
    {
        if (TrackId != previous.TrackId || RecordId != previous.RecordId
            || StartedAt != previous.StartedAt || EndedAt < previous.EndedAt)
            throw new InvalidOperationException("The controlled Record identity or confirmed interval changed during delivery.");
    }

    public static string Query(Guid owner, Guid target, DateTimeOffset since, DateTimeOffset until) => $"""
        select row_to_json(witness) from (
            select t.id as "TrackId", r.id as "RecordId", r.started_at as "StartedAt", r.ended_at as "EndedAt"
            from records r join tracks t on t.id = r.track_id
            join collectors c on c.id = t.collector_id join timelines l on l.id = c.timeline_id
            where l.owner_id = '{owner:D}'::uuid
              and c.key = 'heartbeat.collector.desktop.macos' and c.target = 'verification-{target:N}'
              and t.type = 'desktop.application.foreground' and t.version = 1
              and t.time_mode = 'range' and t.end_mode = 'explicit'
              and r.value->>'device_id' = c.target
              and r.value->'application'->>'platform' = 'macos'
              and r.value->'application'->>'id_kind' = 'bundle_id'
              and r.value->'application'->>'id' = '{ReplayApplication.Identity}'
              and r.started_at >= timestamptz '{since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}'
              and r.ended_at <= timestamptz '{until.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}'
              and r.ended_at >= r.started_at + interval '2 seconds'
            order by r.started_at, r.id limit 1
        ) witness;
        """;

    public static string ProgressQuery(Guid owner, Guid target) => $"""
        with candidate as (
            select r.*, t.type from records r join tracks t on t.id = r.track_id
            join collectors c on c.id = t.collector_id join timelines l on l.id = c.timeline_id
            where l.owner_id = '{owner:D}'::uuid
              and c.key = 'heartbeat.collector.desktop.macos' and c.target = 'verification-{target:N}'
        ), expected as (
            select * from candidate where type = 'desktop.application.foreground'
              and value->'application'->>'id' = '{ReplayApplication.Identity}'
        )
        select json_build_object(
            'records', (select count(*) from candidate),
            'applicationRecords', (select count(*) from candidate where type = 'desktop.application.foreground'),
            'expectedApplicationRecords', (select count(*) from expected),
            'confirmedExpectedRecords', (select count(*) from expected where ended_at >= started_at + interval '2 seconds'),
            'firstExpectedStart', (select min(started_at) from expected),
            'lastExpectedEnd', (select max(ended_at) from expected)
        );
        """;
}
