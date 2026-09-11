using System.Data;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.DTOs.Persons;
using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

/// <summary>Owner-private view. Confirmed relations select facts without changing their content or time.</summary>
public sealed class PersonFactQuery(AppDbContext db)
{
    public Task<PersonFactPage> ReadSegments(string owner, DateTimeOffset? start, DateTimeOffset? end, int offset, int limit, CancellationToken ct) =>
        Read(owner, start, end, offset, limit, true, ct);
    public Task<PersonFactPage> ReadEvents(string owner, DateTimeOffset? start, DateTimeOffset? end, int offset, int limit, CancellationToken ct) =>
        Read(owner, start, end, offset, limit, false, ct);

    private async Task<PersonFactPage> Read(string owner, DateTimeOffset? start, DateTimeOffset? end, int offset, int limit, bool segments, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var personId = await db.Persons.Where(p => p.OwnerId == owner).Select(p => EF.Property<Guid?>(p, "ObjectId")).SingleOrDefaultAsync(ct);
        if (personId is null) return new PersonFactPage([], 0, []);
        var associations = db.RelationMembers.Where(m => m.Relation.OwnerId == owner && m.Relation.Kind == "used-by" && m.Role != "person" &&
                m.Relation.Members.Any(p => p.Role == "person" && p.ObjectId == personId))
            .Select(m => new { m.ObjectId, Start = m.Relation.ValidFrom, End = m.Relation.ValidTo });
        var eligible = db.FactAttributions.Where(at => at.OwnerId == owner);
        IQueryable<FactResponse> query;
        if (segments)
            query = from f in db.Segments
                    join at in eligible on f.Id equals at.Id
                    where f.StartTime < f.EndTime && (start == null || f.EndTime > start) && (end == null || f.StartTime < end) &&
                        (at.FoiId == personId || associations.Any(a => (a.ObjectId == at.DeviceObjectId || a.ObjectId == at.AccountObjectId) &&
                            (a.Start == null || a.Start < f.EndTime && (end == null || a.Start < end)) &&
                            (a.End == null || a.End > f.StartTime && (start == null || a.End > start))))
                    orderby f.StartTime descending, f.Id
                    select new FactResponse { Id = f.Id, StreamId = f.StreamId, FactId = f.FactId, Revision = f.Revision, CollectorId = f.ObserverId,
                        FoiId = f.FoiId, Aspect = f.Aspect, Source = f.Source, AppId = at.AppId, DeviceId = at.DeviceId,
                        Kind = "segment", Start = f.StartTime, End = f.EndTime, Payload = ObservationQuery.ReadPayload(f.Payload) };
        else
            query = from f in db.Events
                    join at in eligible on f.Id equals at.Id
                    where (start == null || f.Timestamp >= start) && (end == null || f.Timestamp < end) &&
                        (at.FoiId == personId || associations.Any(a => (a.ObjectId == at.DeviceObjectId || a.ObjectId == at.AccountObjectId) &&
                            (a.Start == null || a.Start <= f.Timestamp) && (a.End == null || a.End > f.Timestamp)))
                    orderby f.Timestamp descending, f.Id
                    select new FactResponse { Id = f.Id, StreamId = f.StreamId, FactId = f.FactId, Revision = f.Revision, CollectorId = f.ObserverId,
                        FoiId = f.FoiId, Aspect = f.Aspect, Source = f.Source, AppId = at.AppId, DeviceId = at.DeviceId,
                        Kind = "event", OccurredAt = f.Timestamp, Payload = ObservationQuery.ReadPayload(f.Payload) };
        var count = await query.CountAsync(ct);
        var sources = await query.GroupBy(f => f.Source).OrderBy(g => g.Key).Select(g => new PersonSourceCount(g.Key, g.Count())).ToListAsync(ct);
        var facts = await query.Skip(offset).Take(limit).ToListAsync(ct);
        await new ObservationQuery(db).Populate(owner, facts, ct);
        var links = segments ? await associations.ToListAsync(ct) : [];
        var items = facts.Select(f =>
        {
            if (!segments) return new PersonFactItem(f, [], null);
            var lower = start is { } s && s > f.Start ? s : f.Start!.Value;
            var upper = end is { } e && e < f.End ? e : f.End!.Value;
            var objectIds = f.Relations.Where(r => r.Kind is "observed-on" or "application-account-use")
                .SelectMany(r => r.Members).Where(m => m.Role is "device" or "account").Select(m => m.Object.Id).Concat(f.FoiId is { } foi ? [foi] : []).ToHashSet();
            var intervals = f.FoiId == personId ? [new EffectiveInterval(lower, upper)] : links.Where(a => objectIds.Contains(a.ObjectId))
                .Select(a => new EffectiveInterval(a.Start is { } l && l > lower ? l : lower, a.End is { } u && u < upper ? u : upper))
                .Where(i => i.Start < i.End).OrderBy(i => i.Start).ToArray();
            var coverage = new List<EffectiveInterval>();
            foreach (var interval in intervals)
            {
                if (coverage.Count == 0 || coverage[^1].End < interval.Start) coverage.Add(interval);
                else if (coverage[^1].End < interval.End) coverage[^1] = coverage[^1] with { End = interval.End };
            }
            return new PersonFactItem(f, coverage, coverage.Sum(i => (i.End - i.Start).TotalSeconds));
        }).ToArray();
        await transaction.CommitAsync(ct);
        return new PersonFactPage(items, count, sources);
    }
}
