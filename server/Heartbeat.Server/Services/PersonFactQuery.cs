using System.Data;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.DTOs.Persons;
using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

/// <summary>Owner-private self projection. Relations filter facts; they never become fact rows.</summary>
public sealed class PersonFactQuery(AppDbContext db)
{
    private static System.Text.Json.JsonElement ReadPayload(System.Text.Json.JsonDocument payload) => payload.RootElement.Clone();

    public async Task<PersonFactPage> ReadSegments(string owner, DateTimeOffset? start, DateTimeOffset? end, int offset, int limit, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var person = await db.Persons.SingleOrDefaultAsync(p => p.OwnerId == owner, ct);
        if (person is null) return new PersonFactPage([], 0, []);
        var associations = ConfirmedUsage(owner, person.Id);
        var query = db.Segments.Where(f => f.OwnerId == owner && f.StartTime < f.EndTime &&
            (start == null || f.EndTime > start) && (end == null || f.StartTime < end) &&
            db.FactAttributions.Any(at => at.Id == f.Id && (at.PersonId == person.Id || associations.Any(a =>
                (a.DeviceId != null && a.DeviceId == at.DeviceId || a.AccountId != null && a.AccountId == at.AccountId) &&
                (a.Start == null || a.Start < f.EndTime && (end == null || a.Start < end)) &&
                (a.End == null || a.End > f.StartTime && (start == null || a.End > start))))));
        var count = await query.CountAsync(ct);
        var sources = await query.GroupBy(f => f.Source).OrderBy(g => g.Key).Select(g => new PersonSourceCount(g.Key, g.Count())).ToListAsync(ct);
        var facts = await query.OrderByDescending(f => f.StartTime).ThenBy(f => f.Id).Skip(offset).Take(limit)
            .Select(f => new FactResponse
            {
                Id = f.Id, StreamId = f.StreamId, FactId = f.FactId, Revision = f.Revision,
                ObserverId = f.ObserverId, FoiId = f.FoiId, Aspect = f.Aspect, TargetKind = f.TargetKind, TargetId = f.TargetId, Source = f.Source,
                AppId = db.FactAttributions.Where(a => a.Id == f.Id).Select(a => a.AppId).FirstOrDefault(),
                DeviceId = db.FactAttributions.Where(a => a.Id == f.Id).Select(a => a.DeviceId).FirstOrDefault(),
                Start = f.StartTime, End = f.EndTime, Payload = ReadPayload(f.Payload)
            }).ToListAsync(ct);
        var deviceIds = facts.Where(f => f.DeviceId != null).Select(f => f.DeviceId!.Value).Distinct().ToArray();
        var accountIds = facts.Where(f => f.TargetKind == "account").Select(f => f.TargetId!.Value).Distinct().ToArray();
        var links = await associations.Where(a => a.DeviceId != null && deviceIds.Contains(a.DeviceId.Value) ||
            a.AccountId != null && accountIds.Contains(a.AccountId.Value)).ToListAsync(ct);
        var names = await TargetNames(owner, facts, ct);
        var items = facts.Select(f =>
        {
            var lower = start is { } s && s > f.Start ? s : f.Start!.Value;
            var upper = end is { } e && e < f.End ? e : f.End!.Value;
            var intervals = f.TargetKind == "person" ? new[] { new EffectiveInterval(lower, upper) } : links
                .Where(a => f.TargetKind == "account" ? a.AccountId == f.TargetId : a.DeviceId != null && a.DeviceId == f.DeviceId)
                .Select(a => new EffectiveInterval(a.Start is { } l && l > lower ? l : lower, a.End is { } u && u < upper ? u : upper))
                .Where(i => i.Start < i.End).OrderBy(i => i.Start).ToArray();
            var coverage = new List<EffectiveInterval>();
            foreach (var interval in intervals)
            {
                if (coverage.Count == 0 || coverage[^1].End < interval.Start) coverage.Add(interval);
                else if (coverage[^1].End < interval.End) coverage[^1] = coverage[^1] with { End = interval.End };
            }
            return new PersonFactItem(f, coverage, coverage.Sum(i => (i.End - i.Start).TotalSeconds), names[(f.TargetKind!, f.TargetId!.Value)]);
        }).ToArray();
        await transaction.CommitAsync(ct);
        return new PersonFactPage(items, count, sources);
    }
    public async Task<PersonFactPage> ReadEvents(string owner, DateTimeOffset? start, DateTimeOffset? end, int offset, int limit, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var person = await db.Persons.SingleOrDefaultAsync(p => p.OwnerId == owner, ct);
        if (person is null) return new PersonFactPage([], 0, []);
        var associations = ConfirmedUsage(owner, person.Id);
        var query = db.Events.Where(f => f.OwnerId == owner &&
            (start == null || f.Timestamp >= start) && (end == null || f.Timestamp < end) &&
            db.FactAttributions.Any(at => at.Id == f.Id && (at.PersonId == person.Id || associations.Any(a =>
                (a.DeviceId != null && a.DeviceId == at.DeviceId || a.AccountId != null && a.AccountId == at.AccountId) &&
                (a.Start == null || a.Start <= f.Timestamp) && (a.End == null || a.End > f.Timestamp)))));
        var count = await query.CountAsync(ct);
        var sources = await query.GroupBy(f => f.Source).OrderBy(g => g.Key).Select(g => new PersonSourceCount(g.Key, g.Count())).ToListAsync(ct);
        var facts = await query.OrderByDescending(f => f.Timestamp).ThenBy(f => f.Id).Skip(offset).Take(limit)
            .Select(f => new FactResponse
            {
                Id = f.Id, StreamId = f.StreamId, FactId = f.FactId, Revision = f.Revision,
                ObserverId = f.ObserverId, FoiId = f.FoiId, Aspect = f.Aspect, TargetKind = f.TargetKind, TargetId = f.TargetId, Source = f.Source,
                AppId = db.FactAttributions.Where(a => a.Id == f.Id).Select(a => a.AppId).FirstOrDefault(),
                DeviceId = db.FactAttributions.Where(a => a.Id == f.Id).Select(a => a.DeviceId).FirstOrDefault(),
                OccurredAt = f.Timestamp, Payload = ReadPayload(f.Payload)
            }).ToListAsync(ct);
        var names = await TargetNames(owner, facts, ct);
        await transaction.CommitAsync(ct);
        return new PersonFactPage(facts.Select(f => new PersonFactItem(f, [], null, names[(f.TargetKind!, f.TargetId!.Value)])).ToArray(), count, sources);
    }

    private IQueryable<Heartbeat.Server.Entities.PersonAssociation> ConfirmedUsage(string owner, long personId) =>
        db.Relations.Where(r => r.OwnerId == owner && r.Kind == "used-by" && r.Members.Any(m => m.Role == "person" &&
            db.Persons.Any(p => p.Id == personId && p.OwnerId == owner && EF.Property<Guid?>(p, "ObjectId") == m.ObjectId)))
        .Select(r => new Heartbeat.Server.Entities.PersonAssociation
        {
            OwnerId = owner, PersonId = personId, Start = r.ValidFrom, End = r.ValidTo,
            DeviceId = (from m in r.Members
                        join d in db.Devices on (Guid?)m.ObjectId equals EF.Property<Guid?>(d, "ObjectId")
                        where m.Role == "device" select (long?)d.Id).FirstOrDefault(),
            AccountId = (from m in r.Members
                         join a in db.ServiceAccounts on (Guid?)m.ObjectId equals EF.Property<Guid?>(a, "ObjectId")
                         where m.Role == "account" select (long?)a.Id).FirstOrDefault()
        });

    private async Task<Dictionary<(string Kind, long Id), string>> TargetNames(string owner, List<FactResponse> facts, CancellationToken ct)
    {
        var names = new Dictionary<(string, long), string>();
        var devices = facts.Where(f => f.TargetKind == "device").Select(f => f.TargetId!.Value).Distinct().ToArray();
        foreach (var d in await db.Devices.Where(d => d.OwnerId == owner && devices.Contains(d.Id)).ToListAsync(ct))
            names[("device", d.Id)] = d.DeviceName == "" ? d.HardwareId : d.DeviceName;
        var accounts = facts.Where(f => f.TargetKind == "account").Select(f => f.TargetId!.Value).Distinct().ToArray();
        foreach (var a in await db.ServiceAccounts.Where(a => a.OwnerId == owner && accounts.Contains(a.Id)).ToListAsync(ct))
            names[("account", a.Id)] = a.ServiceKey + " · " + (a.ServiceAccountId ?? "历史账号（身份未知）");
        var contexts = facts.Where(f => f.TargetKind == "application-context").Select(f => f.TargetId!.Value).Distinct().ToArray();
        foreach (var c in await db.ApplicationContexts.Where(c => c.OwnerId == owner && contexts.Contains(c.Id))
            .Select(c => new { c.Id, Name = (c.Device.DeviceName == "" ? c.Device.HardwareId : c.Device.DeviceName) + " / " + c.App.DisplayName }).ToListAsync(ct))
            names[("application-context", c.Id)] = c.Name;
        foreach (var f in facts.Where(f => f.TargetKind == "person")) names[("person", f.TargetId!.Value)] = "本人";
        return names;
    }

}
