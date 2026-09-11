using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Data;

public sealed class FactAttribution
{
    public Guid Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public Guid? FoiId { get; set; }
    public long? DeviceId { get; set; }
    public long? AppId { get; set; }
    public long? AccountId { get; set; }
    public long? PersonId { get; set; }
}

public partial class AppDbContext
{
    /// <summary>Device/App associations are bound to this exact fact, never joined by overlapping time.</summary>
    public IQueryable<FactAttribution> FactAttributions =>
        from f in Facts
        join d in Devices on f.FoiId equals EF.Property<Guid?>(d, "ObjectId") into directDevices
        from device in directDevices.DefaultIfEmpty()
        join a in Apps on f.FoiId equals EF.Property<Guid?>(a, "ObjectId") into directApps
        from app in directApps.DefaultIfEmpty()
        join c in ServiceAccounts on f.FoiId equals EF.Property<Guid?>(c, "ObjectId") into directAccounts
        from account in directAccounts.DefaultIfEmpty()
        join p in Persons on f.FoiId equals EF.Property<Guid?>(p, "ObjectId") into directPersons
        from person in directPersons.DefaultIfEmpty()
        select new FactAttribution
        {
            Id = f.Id, OwnerId = f.OwnerId, FoiId = f.FoiId,
            DeviceId = device != null ? (long?)device.Id :
                (from member in RelationMembers
                 join other in Devices on (Guid?)member.ObjectId equals EF.Property<Guid?>(other, "ObjectId")
                 where member.Relation.OwnerId == f.OwnerId && member.Relation.FactId == f.Id && member.Relation.Kind == "observed-on" && member.Role == "device"
                 select (long?)other.Id).FirstOrDefault(),
            AppId = app != null ? (long?)app.Id : account != null ? account.Service.AppId :
                (from member in RelationMembers
                 join other in Apps on (Guid?)member.ObjectId equals EF.Property<Guid?>(other, "ObjectId")
                 where member.Relation.OwnerId == f.OwnerId && member.Relation.FactId == f.Id && member.Relation.Kind == "observed-on" && member.Role == "app"
                 select (long?)other.Id).FirstOrDefault() ?? (f.AppIdentity != null ? (long?)f.AppIdentity.AppId : null),
            AccountId = account != null ? (long?)account.Id : null,
            PersonId = person != null ? (long?)person.Id : null
        };
}
