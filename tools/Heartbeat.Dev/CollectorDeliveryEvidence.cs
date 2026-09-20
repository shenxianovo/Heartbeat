using System.Globalization;
using System.Text.Json.Serialization;

namespace Heartbeat.Dev;

// Only metadata leaves the isolated database. Native titles and application names are never exported.
internal sealed record CollectorDeliveryEvidence(
    [property: JsonRequired] int Timelines,
    [property: JsonRequired] int Collectors,
    [property: JsonRequired] int MatchingCollectors,
    [property: JsonRequired] int Records,
    [property: JsonRequired] int MatchingRecords,
    [property: JsonRequired] int ApplicationRecords,
    [property: JsonRequired] int ValidApplicationRecords)
{
    public void RequireEmpty()
    {
        if (Timelines != 0 || Collectors != 0 || Records != 0)
            throw new InvalidOperationException("Collector delivery requires an empty, isolated database.");
    }

    public void RequireDelivered(int accepted)
    {
        if (accepted <= 0 || Records != accepted || MatchingRecords != accepted)
            throw new InvalidOperationException("Stored Records do not match the Hub custody count, identity or collection time window.");
        if (Timelines != 1 || Collectors != 1 || MatchingCollectors != 1)
            throw new InvalidOperationException("Collector registration did not preserve the expected Owner, key and Target.");
        if (ApplicationRecords <= 0 || ValidApplicationRecords != ApplicationRecords)
            throw new InvalidOperationException("The native snapshot did not produce valid foreground application Records.");
    }

    internal static string Query(Guid owner, Guid target, DateTimeOffset started, DateTimeOffset completed) => $"""
        with matching_collectors as (
            select c.id from collectors c join timelines l on l.id = c.timeline_id
            where l.owner_id = '{owner:D}'::uuid
              and c.key = 'heartbeat.collector.desktop.macos'
              and c.target = 'verification-{target:N}'
        ), matching_records as (
            select r.*, t.type, t.version, t.time_mode, t.end_mode
            from records r join tracks t on t.id = r.track_id
            join matching_collectors c on c.id = t.collector_id
            where r.started_at between {Timestamp(started)} and {Timestamp(completed)}
              and (r.ended_at is null or r.ended_at between r.started_at and {Timestamp(completed)})
              and r.value->>'device_id' = 'verification-{target:N}'
        )
        select json_build_object(
            'Timelines', (select count(*) from timelines),
            'Collectors', (select count(*) from collectors),
            'MatchingCollectors', (select count(*) from matching_collectors),
            'Records', (select count(*) from records),
            'MatchingRecords', (select count(*) from matching_records),
            'ApplicationRecords', (select count(*) from matching_records where type = 'desktop.application.foreground'),
            'ValidApplicationRecords', (select count(*) from matching_records
                where type = 'desktop.application.foreground' and version = 1
                  and time_mode = 'range' and end_mode = 'explicit' and ended_at is not null
                  and value->'application'->>'platform' = 'macos'
                  and value->'application'->>'id_kind' in ('bundle_id', 'executable_path')
                  and jsonb_typeof(value->'application'->'id') = 'string'
                  and btrim(value->'application'->>'id') <> '')
        );
        """;

    private static string Timestamp(DateTimeOffset value) =>
        $"timestamptz '{value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}+00'";
}
