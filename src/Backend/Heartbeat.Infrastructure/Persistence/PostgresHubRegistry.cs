using System.Text.Json;
using Heartbeat.Management;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Persistence;

public sealed class PostgresHubRegistry(HeartbeatDbContext db, TimeProvider clock) : IHubRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> ReportAsync(Guid owner, Guid id, Guid sessionId, HubReport report, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(SavedStatus.From(report), JsonOptions);
        var now = clock.GetUtcNow();
        var cutoff = now - HubManagement.OnlineTimeout;
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO hubs (id, owner_id, session_id, last_seen_at, status)
            VALUES ({id}, {owner}, {sessionId}, {now}, {json}::jsonb)
            ON CONFLICT (id) DO UPDATE SET session_id = EXCLUDED.session_id, last_seen_at = EXCLUDED.last_seen_at, status = EXCLUDED.status
            WHERE hubs.owner_id = EXCLUDED.owner_id AND hubs.retired_at IS NULL
                AND (hubs.session_id = EXCLUDED.session_id OR hubs.last_seen_at <= {cutoff})
            """, cancellationToken);
        return rows == 1;
    }

    public async Task<IReadOnlyList<HubSummary>> ListAsync(Guid owner, CancellationToken cancellationToken)
    {
        var nodes = await db.Hubs.AsNoTracking().Where(x => x.OwnerId == owner)
            .OrderByDescending(x => x.LastSeenAt).ToListAsync(cancellationToken);
        var cutoff = clock.GetUtcNow() - HubManagement.OnlineTimeout;
        return nodes.Select(x => new HubSummary(x.Id, x.LastSeenAt,
            x.RetiredAt is null && x.LastSeenAt > cutoff, x.RetiredAt is not null,
            JsonSerializer.Deserialize<SavedStatus>(x.StatusJson, JsonOptions)!.ToReport())).ToArray();
    }

    public async Task<bool> RetireAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var updated = await db.Hubs.Where(x => x.OwnerId == owner && x.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.RetiredAt, now), cancellationToken) == 1;
        return updated;
    }

    // Persist only the status needed to display an offline node. Configuration and
    // installed-type forms remain live data from the executing Hub.
    private sealed record SavedStatus(string DisplayName, string Kind, StatusCollector[] Collectors, DeliveryState Delivery)
    {
        public static SavedStatus From(HubReport report) => new(report.DisplayName, report.Kind,
            report.Collectors.Select(x => new StatusCollector(x.Key, x.Target, x.DisplayName, x.State, x.Error)).ToArray(), report.Delivery);
        public HubReport ToReport() => new(DisplayName, Kind, [],
            Collectors.Select(x => new CollectorState(x.Key, x.Target, x.DisplayName, x.State, x.Error)).ToArray(), Delivery);
    }
    private sealed record StatusCollector(string Key, string Target, string DisplayName, string State, string? Error);
}
