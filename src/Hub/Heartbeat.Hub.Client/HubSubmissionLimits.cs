namespace Heartbeat.Hub;

/// <summary>Shared limits for Collector-to-Hub submissions and persistent custody.</summary>
public static class HubSubmissionLimits
{
    public const int MaximumBatchSize = 500;
    public const int MaximumBatchBytes = 1_048_576;
}
