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
    [HttpGet]
    [EndpointName("getMyPersonSettings")]
    public async Task<ActionResult<PersonSettingsResponse>> Get(CancellationToken ct)
    {
        var owner = currentUser.GetUserId();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var person = await db.Persons.SingleOrDefaultAsync(p => p.OwnerId == owner, ct);
        var targets = await db.Devices.Where(d => d.OwnerId == owner).OrderBy(d => d.DeviceName).ThenBy(d => d.Id)
            .Select(d => new PersonTargetOption("device", d.Id, d.DeviceName == "" ? d.HardwareId : d.DeviceName)).ToListAsync(ct);
        targets.AddRange(await db.ServiceAccounts.Where(a => a.OwnerId == owner).OrderBy(a => a.ServiceKey).ThenBy(a => a.Id)
            .Select(a => new PersonTargetOption("account", a.Id, a.ServiceKey + " · " + (a.ServiceAccountId ?? "历史账号（身份未知）"))).ToListAsync(ct));
        var links = await db.PersonAssociations.Where(a => a.OwnerId == owner).OrderBy(a => a.Id)
            .Select(a => new PersonAssociationResponse(a.Id, a.DeviceId, a.AccountId, a.Start, a.End)).ToListAsync(ct);
        await transaction.CommitAsync(ct);
        return new PersonSettingsResponse(person is null ? null : new PersonResponse(person.Id, person.Reference), targets, links);
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
        var person = await db.Persons.SingleAsync(p => p.OwnerId == owner, ct);
        return new PersonResponse(person.Id, person.Reference);
    }

    [HttpPost("associations")]
    [EndpointName("createMyPersonAssociation")]
    public Task<ActionResult<PersonAssociationResponse>> Create(PersonAssociationRequest request, CancellationToken ct) => Save(null, request, ct);

    [HttpPut("associations/{id:long}")]
    [EndpointName("correctMyPersonAssociation")]
    public Task<ActionResult<PersonAssociationResponse>> Correct(long id, PersonAssociationRequest request, CancellationToken ct) => Save(id, request, ct);

    private async Task<ActionResult<PersonAssociationResponse>> Save(long? id, PersonAssociationRequest request, CancellationToken ct)
    {
        var owner = currentUser.GetUserId();
        var person = await db.Persons.SingleOrDefaultAsync(p => p.OwnerId == owner, ct);
        if (person is null) return NotFound();
        if (!ValidBound(request.Start) || !ValidBound(request.End)) return BadRequest("Use finite timestamps with at most microsecond precision, or explicit null bounds.");
        if ((request.DeviceId is null) == (request.AccountId is null) ||
            request.Start is { } start && request.End is { } end && start >= end) return BadRequest("Choose a device or account and a nonempty interval.");
        if (request.DeviceId is { } device && !await db.Devices.AnyAsync(d => d.OwnerId == owner && d.Id == device, ct) ||
            request.AccountId is { } account && !await db.ServiceAccounts.AnyAsync(a => a.OwnerId == owner && a.Id == account, ct)) return NotFound();
        var association = id is { } existing ? await db.PersonAssociations.SingleOrDefaultAsync(a => a.OwnerId == owner && a.Id == existing, ct)
            : new PersonAssociation { OwnerId = owner, PersonId = person.Id };
        if (association is null) return NotFound();
        association.DeviceId = request.DeviceId; association.AccountId = request.AccountId;
        association.Start = request.Start?.ToUniversalTime(); association.End = request.End?.ToUniversalTime();
        if (id is null) db.PersonAssociations.Add(association);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.ForeignKeyViolation })
        { return NotFound(); } // Reference removed concurrently after validation.
        catch (DbUpdateConcurrencyException) { return NotFound(); }
        return new PersonAssociationResponse(association.Id, association.DeviceId, association.AccountId, association.Start, association.End);
    }

    [HttpDelete("associations/{id:long}")]
    [EndpointName("removeMyPersonAssociation")]
    public async Task<IActionResult> Remove(long id, CancellationToken ct)
    {
        var owner = currentUser.GetUserId();
        var removed = await db.PersonAssociations.Where(a => a.OwnerId == owner && a.Id == id).ExecuteDeleteAsync(ct);
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
