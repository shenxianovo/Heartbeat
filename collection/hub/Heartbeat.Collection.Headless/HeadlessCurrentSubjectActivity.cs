namespace Heartbeat.Collection.Headless;

public sealed record HeadlessCurrentSubjectActivity(
    string? Title,
    string? IdentityKey,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    object? Attributes);
