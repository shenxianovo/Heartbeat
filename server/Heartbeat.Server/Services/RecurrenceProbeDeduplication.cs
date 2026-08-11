using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Entities;

namespace Heartbeat.Server.Services;

internal static class RecurrenceProbeDeduplication
{
    public static IOrderedEnumerable<RecurrenceProbe> OrderForKeep(IEnumerable<RecurrenceProbe> probes)
        => probes
            .OrderByDescending(x => StatusPriority(x.Status))
            .ThenBy(x => x.ResolvedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id);

    private static int StatusPriority(string status) => status switch
    {
        ProbeStatuses.Promoted => 4,
        ProbeStatuses.Muted => 3,
        ProbeStatuses.Denied => 2,
        ProbeStatuses.Active => 1,
        _ => 0
    };
}
