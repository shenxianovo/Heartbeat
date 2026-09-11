using System.Data;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

/// <summary>Attach object identities and exact fact evidence to any bounded read projection.</summary>
internal sealed class ObservationQuery(AppDbContext db)
{
    public static System.Text.Json.JsonElement ReadPayload(System.Text.Json.JsonDocument value) => value.RootElement.Clone();
    public async Task<List<T>> Read<T>(string owner, IQueryable<T> query, CancellationToken ct = default) where T : ObservationResponse
    {
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var rows = await query.ToListAsync(ct);
        await Populate(owner, rows, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return rows;
    }

    public async Task Populate(string owner, IEnumerable<ObservationResponse> observations, CancellationToken ct = default)
    {
        var rows = observations.ToArray();
        if (rows.Length == 0) return;
        var ids = rows.Select(f => f.Id).ToArray();
        var foiIds = rows.Where(f => f.FoiId != null).Select(f => f.FoiId!.Value).Distinct().ToArray();
        var objects = await db.Objects.AsNoTracking().Where(o => foiIds.Contains(o.Id) && (o.OwnerId == owner || o.OwnerId == null))
            .Select(o => new ObjectSummary(o.Id, o.Kind, o.Scope, o.Key, o.Name)).ToDictionaryAsync(o => o.Id, ct);
        var relations = await db.Relations.AsNoTracking().Where(r => r.OwnerId == owner && r.FactId != null && ids.Contains(r.FactId.Value))
            .Include(r => r.Members).ThenInclude(m => m.Object).OrderBy(r => r.Id).ToListAsync(ct);
        var byFact = relations.ToLookup(r => r.FactId!.Value, r => new RelationResponse(r.Id, r.Kind, r.ValidFrom, r.ValidTo,
            r.Evidence.RootElement.Clone(), r.Members.OrderBy(m => m.Role).ThenBy(m => m.ObjectId)
                .Select(m => new RelationMemberResponse(m.Role, new ObjectSummary(m.Object.Id, m.Object.Kind, m.Object.Scope, m.Object.Key, m.Object.Name))).ToArray()));
        foreach (var row in rows)
        {
            row.Foi = row.FoiId is { } id ? objects.GetValueOrDefault(id) : null;
            row.Relations = byFact[row.Id].ToArray();
        }
        foreach (var relation in relations) relation.Evidence.Dispose();
    }
}
