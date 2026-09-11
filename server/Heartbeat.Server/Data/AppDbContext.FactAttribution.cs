using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Data;

public sealed class FactAttribution
{
    public Guid Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public Guid? FoiId { get; set; }
    public Guid? DeviceObjectId { get; set; }
    public Guid? AppObjectId { get; set; }
    public Guid? AccountObjectId { get; set; }
    public long? DeviceId { get; set; }
    public long? AppId { get; set; }
    public long? AccountId { get; set; }
    public long? PersonId { get; set; }
}

public partial class AppDbContext
{
    /// <summary>Objects establish identity; product metadata is optional. Relations name this exact fact.</summary>
    public IQueryable<FactAttribution> FactAttributions =>
        from f in Facts
        join o in Objects on f.FoiId equals (Guid?)o.Id into fois
        from foi in fois.DefaultIfEmpty()
        let deviceObjectId = foi != null && foi.Kind == "machine" ? (Guid?)foi.Id : RelationMembers.Where(m => m.Relation.OwnerId == f.OwnerId && m.Relation.FactId == f.Id &&
            (m.Relation.Kind == "observed-on" || m.Relation.Kind == "application-account-use") && m.Role == "device").Select(m => (Guid?)m.ObjectId).FirstOrDefault()
        let appObjectId = foi != null && foi.Kind == "app" ? (Guid?)foi.Id : RelationMembers.Where(m => m.Relation.OwnerId == f.OwnerId && m.Relation.FactId == f.Id &&
            (m.Relation.Kind == "observed-on" || m.Relation.Kind == "application-account-use") && m.Role == "app").Select(m => (Guid?)m.ObjectId).FirstOrDefault()
        let accountObjectId = foi != null && foi.Kind == "account" ? (Guid?)foi.Id : RelationMembers.Where(m => m.Relation.OwnerId == f.OwnerId && m.Relation.FactId == f.Id &&
            (m.Relation.Kind == "observed-on" || m.Relation.Kind == "application-account-use") && m.Role == "account").Select(m => (Guid?)m.ObjectId).FirstOrDefault()
        join d in Devices on deviceObjectId equals EF.Property<Guid?>(d, "ObjectId") into devices
        from device in devices.DefaultIfEmpty()
        join a in Apps on appObjectId equals EF.Property<Guid?>(a, "ObjectId") into apps
        from app in apps.DefaultIfEmpty()
        join c in ServiceAccounts on accountObjectId equals EF.Property<Guid?>(c, "ObjectId") into accounts
        from account in accounts.DefaultIfEmpty()
        join p in Persons on f.FoiId equals EF.Property<Guid?>(p, "ObjectId") into persons
        from person in persons.DefaultIfEmpty()
        select new FactAttribution
        {
            Id = f.Id, OwnerId = f.OwnerId, FoiId = f.FoiId,
            DeviceObjectId = deviceObjectId, AppObjectId = appObjectId, AccountObjectId = accountObjectId,
            DeviceId = device != null ? (long?)device.Id : null,
            AppId = app != null ? (long?)app.Id : account != null ? account.Service.AppId : f.AppIdentity != null ? f.AppIdentity!.AppId : null,
            AccountId = account != null ? (long?)account.Id : null,
            PersonId = person != null ? (long?)person.Id : null
        };
}
