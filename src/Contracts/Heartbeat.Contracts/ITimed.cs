namespace Heartbeat.Contracts;

public interface ITimed
{
    DateTimeOffset? StartAt { get; }
    DateTimeOffset? EndAt { get; }
}
