namespace Heartbeat.Core;

public interface ITimed
{
    DateTimeOffset? StartAt { get; }
    DateTimeOffset? EndAt { get; }
}
