namespace Heartbeat.Server.Entities;

public static class AppCatalogOverrideStatuses
{
    public const string Active = "active";
    public const string Promoted = "promoted";
    public const string Deleted = "deleted";
}

/// <summary>
/// Deployment-local administrator intent. AppIdentity.AppId is only the effective result;
/// this row is the durable reason that the Reconciler must continue choosing TargetApp.
/// </summary>
public sealed class AppCatalogOverride
{
    public long Id { get; set; }
    public long AppIdentityId { get; set; }
    public long? TargetAppId { get; set; }
    public string TargetAppKey { get; set; } = string.Empty;
    public string Status { get; set; } = AppCatalogOverrideStatuses.Active;
    public string CreatedBySubject { get; set; } = string.Empty;
    public string UpdatedBySubject { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PromotedAt { get; set; }

    public AppIdentity AppIdentity { get; set; } = null!;
    public App? TargetApp { get; set; }
}
