using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.DTOs.Persons;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Controllers;

[ApiController]
[Route("api/v1/me/person")]
[Authorize]
public sealed class PersonController(AppDbContext db, ICurrentUserService currentUser) : ControllerBase
{
    private Task<PersonResponse?> Person(string owner, CancellationToken ct) => db.Persons.Where(p => p.OwnerId == owner)
        .Select(p => new PersonResponse(EF.Property<Guid?>(p, "ObjectId")!.Value, p.Reference)).SingleOrDefaultAsync(ct);

    [HttpGet]
    [EndpointName("getMyPersonSettings")]
    public async Task<ActionResult<PersonSettingsResponse>> Get(CancellationToken ct)
    {
        var owner = currentUser.GetUserId();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var person = await Person(owner, ct);
        var objects = await db.Objects.Where(o => o.OwnerId == owner && (o.Kind == "machine" || o.Kind == "account"))
            .OrderBy(o => o.Kind).ThenBy(o => o.Name).ThenBy(o => o.Id)
            .Select(o => new ObjectSummary(o.Id, o.Kind, o.Scope, o.Key, o.Name)).ToListAsync(ct);
        var links = person is null ? [] : await db.Relations.Where(r => r.OwnerId == owner && r.Kind == "used-by" &&
                r.Members.Any(m => m.Role == "person" && m.ObjectId == person.Id)).OrderBy(r => r.Id)
            .Select(r => new PersonAssociationResponse(r.Id, r.Members.Where(m => m.Role != "person").Select(m => m.ObjectId).Single(), r.ValidFrom, r.ValidTo)).ToListAsync(ct);
        await transaction.CommitAsync(ct);
        return new PersonSettingsResponse(person, objects, links);
    }

    [HttpPut]
    [EndpointName("establishMyPerson")]
    public async Task<ActionResult<PersonResponse>> Establish(CancellationToken ct)
    {
        var owner = currentUser.GetUserId();
        if (!await db.Users.AnyAsync(u => u.Id == owner, ct)) return NotFound();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Persons" ("OwnerId", "Reference") VALUES ({owner}, {Guid.NewGuid()})
            ON CONFLICT ("OwnerId") DO NOTHING
            """, ct);
        return (await Person(owner, ct))!;
    }

    [HttpPost("associations")]
    [EndpointName("createMyPersonAssociation")]
    public Task<ActionResult<PersonAssociationResponse>> Create(PersonAssociationRequest request, CancellationToken ct) => Save(null, request, ct);

    [HttpPut("associations/{id:guid}")]
    [EndpointName("correctMyPersonAssociation")]
    public Task<ActionResult<PersonAssociationResponse>> Correct(Guid id, PersonAssociationRequest request, CancellationToken ct) => Save(id, request, ct);

    private async Task<ActionResult<PersonAssociationResponse>> Save(Guid? id, PersonAssociationRequest request, CancellationToken ct)
    {
        var owner = currentUser.GetUserId();
        var person = await Person(owner, ct);
        if (person is null) return NotFound();
        if (!ValidBound(request.Start) || !ValidBound(request.End)) return BadRequest("Use finite timestamps with at most microsecond precision, or explicit null bounds.");
        if (request.Start is { } start && request.End is { } end && start >= end) return BadRequest("Use a nonempty interval.");
        var used = await db.Objects.SingleOrDefaultAsync(o => o.Id == request.ObjectId && o.OwnerId == owner && (o.Kind == "machine" || o.Kind == "account"), ct);
        if (used is null) return NotFound();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var relation = id is { } existing ? await db.Relations.Include(r => r.Members).SingleOrDefaultAsync(r => r.Id == existing && r.OwnerId == owner &&
            r.Kind == "used-by" && r.Members.Any(m => m.Role == "person" && m.ObjectId == person.Id), ct)
            : new ObjectRelation { OwnerId = owner, Kind = "used-by", Evidence = JsonSerializer.SerializeToDocument(new { confirmation = "owner" }),
                Members = [new RelationMember { Role = "person", ObjectId = person.Id }] };
        if (relation is null) return NotFound();
        relation.ValidFrom = request.Start?.ToUniversalTime(); relation.ValidTo = request.End?.ToUniversalTime();
        var member = relation.Members.SingleOrDefault(m => m.Role != "person");
        if (member?.ObjectId != used.Id)
        {
            if (member is not null) { relation.Members.Remove(member); db.RelationMembers.Remove(member); }
            relation.Members.Add(new RelationMember { Role = used.Kind == "machine" ? "device" : "account", ObjectId = used.Id });
        }
        if (id is null) db.Relations.Add(relation);
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.ForeignKeyViolation })
        { return NotFound(); }
        catch (DbUpdateConcurrencyException) { return NotFound(); }
        return new PersonAssociationResponse(relation.Id, used.Id, relation.ValidFrom, relation.ValidTo);
    }

    [HttpDelete("associations/{id:guid}")]
    [EndpointName("removeMyPersonAssociation")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var removed = await db.Relations.Where(r => r.OwnerId == currentUser.GetUserId() && r.Kind == "used-by" && r.Id == id).ExecuteDeleteAsync(ct);
        return removed == 0 ? NotFound() : NoContent();
    }

    [HttpGet("facts/segments")]
    [EndpointName("getMyPersonSegments")]
    public async Task<ActionResult<PersonFactPage>> Segments(DateTimeOffset? start, DateTimeOffset? end, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (!ValidBound(start) || !ValidBound(end) || start >= end || offset < 0 || limit is < 1 or > 200) return BadRequest("Invalid query window or page.");
        return await new PersonFactQuery(db).ReadSegments(currentUser.GetUserId(), start?.ToUniversalTime(), end?.ToUniversalTime(), offset, limit, ct);
    }
    [HttpGet("facts/events")]
    [EndpointName("getMyPersonEvents")]
    public async Task<ActionResult<PersonFactPage>> Events(DateTimeOffset? start, DateTimeOffset? end, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (!ValidBound(start) || !ValidBound(end) || start >= end || offset < 0 || limit is < 1 or > 200) return BadRequest("Invalid query window or page.");
        return await new PersonFactQuery(db).ReadEvents(currentUser.GetUserId(), start?.ToUniversalTime(), end?.ToUniversalTime(), offset, limit, ct);
    }

    private static bool ValidBound(DateTimeOffset? value) => value is null ||
        value.Value.UtcTicks % 10 == 0 && value > DateTimeOffset.MinValue && value < DateTimeOffset.MaxValue;
}
