namespace Heartbeat.Server.Entities;

public sealed class ServiceAccount
{
    public long Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string ServiceKey { get; set; } = string.Empty;
    public string? ServiceAccountId { get; set; }
    // Pre-account data only. Never substituted with the currently authenticated account.
    public Guid? LegacySubjectId { get; set; }
    public ServiceProduct Service { get; set; } = null!;
}

public sealed class ServiceProduct
{
    public string ServiceKey { get; set; } = string.Empty;
    public long AppId { get; set; }
    public App App { get; set; } = null!;
}
