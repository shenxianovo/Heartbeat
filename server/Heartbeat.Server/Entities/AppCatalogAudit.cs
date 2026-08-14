namespace Heartbeat.Server.Entities;

public sealed class AppCatalogAudit
{
    public long Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int? SchemaVersion { get; set; }
    public int? CatalogVersion { get; set; }
    public string? ContentHash { get; set; }
    public string? ActorSubject { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string SummaryJson { get; set; } = "{}";
}
