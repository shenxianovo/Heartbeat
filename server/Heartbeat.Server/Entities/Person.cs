namespace Heartbeat.Server.Entities;

/// <summary>The explicitly established self, separate from authentication and data ownership.</summary>
public sealed class Person
{
    public long Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public Guid Reference { get; set; }
}

/// <summary>One confirmed [Start, End) usage interval; null bounds are unbounded.</summary>
public sealed class PersonAssociation
{
    public long Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public long PersonId { get; set; }
    public long? DeviceId { get; set; }
    public long? AccountId { get; set; }
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }
}
