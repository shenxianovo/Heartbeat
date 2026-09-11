using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed partial class FactStore
{
    private sealed record ObjectResolution(Guid Id, long? AppIdentityId = null);
    private sealed record ObservationMember(string Role, Guid ObjectId);
    private sealed record ObservationRelation(string Kind, List<ObservationMember> Members);
    private sealed record ObservationState(Guid? CollectorId, Guid? FoiId, long? AppIdentityId, List<ObservationRelation> Relations);

    private async Task<ObservationState> ResolveObservation(FactStream stream, FactSnapshot snapshot,
        JsonElement payload, string? aspect, CancellationToken ct, long? oldAppIdentityId = null)
    {
        if (snapshot.Relations is null)
            return await ResolveLegacyObservation(stream, snapshot, payload, aspect, ct, oldAppIdentityId);
        if (snapshot.ObserverId is not null || snapshot.Target is not null)
            throw new FactIngestException("A Fact cannot mix object references with the retired Target envelope.");
        if (snapshot.CollectorId == Guid.Empty || snapshot.Relations.Count > 8)
            throw new FactIngestException("Invalid observation envelope.");
        var foi = snapshot.Foi is null ? null : await ResolveObject(stream.OwnerId, snapshot.Foi, ct);
        var relations = new List<ObservationRelation>();
        long? appIdentityId = foi?.AppIdentityId;
        foreach (var relation in snapshot.Relations)
        {
            if (relation is null || relation.Members is null || relations.Any(r => r.Kind == relation.Kind))
                throw new FactIngestException("Invalid or duplicate Fact relation.");
            var roles = relation.Kind switch
            {
                "observed-on" or "installed-on" => new[] { "app", "device" },
                "application-account-use" => new[] { "account", "app", "device" },
                _ => throw new FactIngestException("Unsupported observed relation kind.")
            };
            if (relation.Members.Count != roles.Length ||
                !relation.Members.Select(m => m?.Role).Order().SequenceEqual(roles))
                throw new FactIngestException("Relation members do not match its contract.");
            var members = new List<ObservationMember>();
            foreach (var member in relation.Members)
            {
                if (member.Object is null || member.Object.Kind != (member.Role == "device" ? "machine" : member.Role))
                    throw new FactIngestException("Relation role does not match the Object kind.");
                var resolved = await ResolveObject(stream.OwnerId, member.Object, ct);
                if (resolved.AppIdentityId is { } identity)
                {
                    if (appIdentityId is not null && appIdentityId != identity)
                        throw new FactIngestException("One Fact cannot claim different platform App identities.");
                    appIdentityId = identity;
                }
                members.Add(new(member.Role, resolved.Id));
            }
            if (foi is not null && !members.Any(m => m.ObjectId == foi.Id))
                throw new FactIngestException("A Fact relation must include its directly observed Object.");
            if (members.Any(member => relations.SelectMany(r => r.Members)
                    .Any(existing => existing.Role == member.Role && existing.ObjectId != member.ObjectId)))
                throw new FactIngestException("One Fact cannot claim conflicting Objects for the same relation role.");
            relations.Add(new(relation.Kind, members));
        }
        return new(snapshot.CollectorId, foi?.Id, appIdentityId, relations);
    }

    private async Task<ObjectResolution> ResolveObject(string owner, ObservationObjectReference reference, CancellationToken ct)
    {
        if (reference.Kind is not ("machine" or "app" or "account" or "person") ||
            string.IsNullOrWhiteSpace(reference.Scope) || reference.Scope.Length > 128 || reference.Scope != reference.Scope.Trim() ||
            string.IsNullOrWhiteSpace(reference.Key) || reference.Key.Length > 1024 || reference.Key != reference.Key.Trim())
            throw new FactIngestException("Invalid Object reference.");
        if (reference.Kind == "machine" && reference.Scope == ObservationObjectScopes.Machine)
        {
            if (reference.Key.Length > 256 || reference.Key.StartsWith("subject:", StringComparison.Ordinal))
                throw new FactIngestException("A machine requires an observed device identity.");
            var device = await new DeviceService(db).ResolveFactReferenceAsync(owner, reference.Key, null, ct);
            return new(await ObjectId(device, ct));
        }
        if (reference.Kind == "app" && reference.Scope == ObservationObjectScopes.AppIdentity)
        {
            AppIdentity identity;
            try { identity = await new AppIdentityService(db).ResolveAsync(reference.Key, cancellationToken: ct); }
            catch (ArgumentException ex) { throw new FactIngestException(ex.Message); }
            var id = await db.Apps.Where(a => a.Id == identity.AppId).Select(a => EF.Property<Guid>(a, "ObjectId")).SingleAsync(ct);
            return new(id, identity.Id);
        }
        if (reference.Kind == "app" && reference.Scope == ObservationObjectScopes.App)
        {
            if (reference.Key.Length > 128) throw new FactIngestException("App product key is too long.");
            var key = reference.Key;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            App? app;
            while ((app = await db.Apps.SingleOrDefaultAsync(a => a.Key == key, ct)) is null)
            {
                if (!visited.Add(key)) throw new FactIngestException("App merge references contain a cycle.");
                var next = await db.AppMergeReceipts.Where(r => r.SourceAppKey == key)
                    .OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.Id)
                    .Select(r => r.TargetAppKey).FirstOrDefaultAsync(ct);
                if (next is null) break;
                key = next;
            }
            if (app is null)
            {
                app = new App { Key = key, DisplayName = key };
                db.Apps.Add(app);
            }
            return new(await ObjectId(app, ct));
        }
        if (reference.Kind == "person")
        {
            if (reference.Scope != ObservationObjectScopes.Person || !Guid.TryParse(reference.Key, out var personReference))
                throw new FactIngestException("A Person requires its established reference.");
            var person = await db.Persons.SingleOrDefaultAsync(p => p.OwnerId == owner && p.Reference == personReference, ct)
                ?? throw new FactIngestException("Person Object must already exist within its Owner.");
            return new(await ObjectId(person, ct));
        }
        if (reference.Kind == "account" && reference.Scope == "vrchat")
        {
            ServiceAccountReference? accountReference = ServiceAccountReference.IsVRChatAccountId(reference.Key)
                ? new("vrchat", reference.Key) : null;
            var subjectId = Guid.Empty;
            if (accountReference is null && (!Guid.TryParse(reference.Key, out subjectId) || subjectId == Guid.Empty))
                throw new FactIngestException("Invalid VRChat account identity.");
            var account = await new ServiceAccountService(db).ResolveAsync(owner, accountReference, subjectId, ct);
            return new(await ObjectId(account, ct));
        }
        if (reference.Kind == "machine" || reference.Kind == "app" && reference.Scope != ObservationObjectScopes.App)
            throw new FactIngestException("Unsupported Object identity scope.");
        var objectOwner = reference.Kind == "app" ? null : owner;
        var value = await db.Objects.SingleOrDefaultAsync(o => o.OwnerId == objectOwner && o.Kind == reference.Kind &&
            o.Scope == reference.Scope && o.Key == reference.Key, ct);
        if (value is null)
        {
            value = new ObservationObject { Id = Guid.CreateVersion7(), OwnerId = objectOwner,
                Kind = reference.Kind, Scope = reference.Scope, Key = reference.Key };
            db.Objects.Add(value);
            await db.SaveChangesAsync(ct);
        }
        return new(value.Id);
    }

    private async Task<Guid> ObjectId<T>(T value, CancellationToken ct) where T : class
    {
        await db.SaveChangesAsync(ct);
        return db.Entry(value).Property<Guid?>("ObjectId").CurrentValue
            ?? throw new FactIngestException("Object identity was not persisted.");
    }

    private async Task<ObservationState> ResolveLegacyObservation(FactStream stream, FactSnapshot snapshot,
        JsonElement payload, string? aspect, CancellationToken ct, long? oldAppIdentityId = null)
    {
        if (snapshot.CollectorId is not null || snapshot.Foi is not null)
            throw new FactIngestException("Object-based Facts must include a Relations list, empty when no relation is known.");
        var appIdentityId = await ResolveApp(stream, payload, aspect, ct) ?? oldAppIdentityId;
        Guid? appObject = appIdentityId is null ? null : await db.AppIdentities.Where(a => a.Id == appIdentityId)
            .Select(a => EF.Property<Guid?>(a.App, "ObjectId")).SingleAsync(ct);
        Guid? deviceObject = null, foi = null, collector = snapshot.ObserverId;
        if (snapshot.Target is { } target)
        {
            if (collector is null || collector == Guid.Empty) throw new FactIngestException("Old Target envelope requires its Observer.");
            try
            {
                switch (target.Kind)
                {
                    case "device":
                        foi = deviceObject = (await ResolveObject(stream.OwnerId, new("machine", ObservationObjectScopes.Machine, target.Reference), ct)).Id;
                        break;
                    case "application-context":
                        var context = ApplicationContextReference.Parse(target.Reference);
                        var resolved = await ResolveObject(stream.OwnerId, new("app", ObservationObjectScopes.AppIdentity, context.AppIdentityKey), ct);
                        if (appIdentityId is not null && appIdentityId != resolved.AppIdentityId)
                            throw new FactIngestException("Old application reference conflicts with its platform identity.");
                        foi = appObject = resolved.Id;
                        appIdentityId = resolved.AppIdentityId;
                        deviceObject = (await ResolveObject(stream.OwnerId, new("machine", ObservationObjectScopes.Machine, context.DeviceReference), ct)).Id;
                        break;
                    case "account":
                        var account = ServiceAccountReference.Parse(target.Reference);
                        if (appIdentityId is not null) throw new FactIngestException("An old account Fact cannot claim a platform App identity.");
                        foi = (await ResolveObject(stream.OwnerId, new("account", account.ServiceKey, account.ServiceAccountId), ct)).Id;
                        break;
                    case "person":
                        foi = (await ResolveObject(stream.OwnerId, new("person", ObservationObjectScopes.Person, PersonReference.Parse(target.Reference).Id.ToString("D")), ct)).Id;
                        break;
                    default: throw new FactIngestException("Unsupported old Target reference.");
                }
            }
            catch (ArgumentException ex) when (ex is not FactIngestException) { throw new FactIngestException(ex.Message); }
        }
        else
        {
            if (collector is not null) throw new FactIngestException("Old Observer envelope requires its Target.");
            if (stream.Source == "vrchat.account" && stream.Subject.Kind == "account")
            {
                var account = await new ServiceAccountService(db).ResolveAsync(stream.OwnerId, null, stream.SubjectId, ct);
                foi = await ObjectId(account, ct);
                collector = stream.Origin == "native" ? stream.CollectorInstanceId : null;
                appIdentityId = null;
            }
            else if (stream.Subject.Kind == "machine" && stream.Subject.DeviceId is { } deviceId)
            {
                deviceObject = await db.Devices.Where(d => d.Id == deviceId && d.OwnerId == stream.OwnerId)
                    .Select(d => EF.Property<Guid?>(d, "ObjectId")).SingleAsync(ct);
                foi = stream.Source == "browser" && appObject is not null ? appObject : deviceObject;
                using var dimensions = JsonDocument.Parse(stream.Dimensions);
                collector = stream.Origin != "native" ? null : stream.Source == "system" ? stream.CollectorInstanceId : stream.Source == "browser"
                    ? Heartbeat.Core.Facts.BrowserFactAttribution.Observer(String(dimensions.RootElement, "externalHostIdentity")) : null;
            }
        }
        return new(collector, foi, appIdentityId, deviceObject is { } device && appObject is { } app
            ? [new("observed-on", [new("device", device), new("app", app)])] : []);
    }

    private async Task<bool> SameRelations(Guid factId, List<ObservationRelation> expected, CancellationToken ct)
    {
        var stored = await db.Relations.Where(r => r.FactId == factId).Include(r => r.Members).ToListAsync(ct);
        return stored.Count == expected.Count && expected.All(e => stored.Any(s => s.Kind == e.Kind &&
            s.Members.Count == e.Members.Count && e.Members.All(m => s.Members.Any(n => n.Role == m.Role && n.ObjectId == m.ObjectId))));
    }

    private async Task WriteRelations(IFactRecord fact, List<ObservationRelation> expected, CancellationToken ct)
    {
        var stored = await db.Relations.Where(r => r.OwnerId == fact.OwnerId && r.FactId == fact.Id).Include(r => r.Members).ToListAsync(ct);
        db.Relations.RemoveRange(stored.Where(r => expected.All(e => e.Kind != r.Kind)));
        foreach (var relation in expected)
        {
            var value = stored.SingleOrDefault(r => r.Kind == relation.Kind);
            if (value is null)
            {
                value = new ObjectRelation { Id = Guid.CreateVersion7(), OwnerId = fact.OwnerId, Kind = relation.Kind,
                    Evidence = JsonDocument.Parse(JsonSerializer.Serialize(new { factId = fact.Id })) };
                db.Relations.Add(value);
            }
            value.ValidFrom = fact is Segment segment ? segment.StartTime : ((Event)fact).Timestamp;
            value.ValidTo = fact is Segment interval ? interval.EndTime : value.ValidFrom;
            foreach (var member in value.Members.Where(m => !relation.Members.Contains(new(m.Role, m.ObjectId))).ToList())
                db.RelationMembers.Remove(member);
            foreach (var member in relation.Members.Where(m => !value.Members.Any(n => n.Role == m.Role && n.ObjectId == m.ObjectId)))
                value.Members.Add(new RelationMember { RelationId = value.Id, Role = member.Role, ObjectId = member.ObjectId });
        }
        await db.SaveChangesAsync(ct);
    }
}
