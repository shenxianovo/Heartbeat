namespace Heartbeat.Server.Entities;

/// <summary>The explicitly established self, separate from authentication and data ownership.</summary>
public sealed class Person
{
    public long Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public Guid Reference { get; set; }
}
