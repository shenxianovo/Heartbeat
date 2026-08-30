using System.Reflection;
using System.Text.Json;

namespace Heartbeat.Core;

/// <summary>
/// Collector-side boundary for rotating a continuously growing Segment before Analytics reaches
/// its strict maximum duration. The one-hour margin covers snapshot cadence and clock skew.
/// </summary>
public static class SegmentRotationPolicy
{
    private const string ResourceName = "Heartbeat.Core.segment-rotation-policy.json";
    private static readonly RotationContract Contract = LoadContract();

    public static readonly TimeSpan RotateAfter =
        TimeSpan.FromMilliseconds(Contract.RotateAfterMilliseconds);
    public static readonly TimeSpan UploadAndClockTolerance =
        SegmentValidationPolicy.MaxDuration - RotateAfter;

    static SegmentRotationPolicy()
    {
        if (Contract.SchemaVersion != 1)
            throw new InvalidOperationException("Unsupported Segment rotation policy schema version.");
        if (TimeSpan.FromMilliseconds(Contract.MaxDurationMilliseconds)
            != SegmentValidationPolicy.MaxDuration)
            throw new InvalidOperationException(
                "Segment rotation contract does not match SegmentValidationPolicy.MaxDuration.");
        if (RotateAfter <= TimeSpan.Zero || RotateAfter >= SegmentValidationPolicy.MaxDuration)
            throw new InvalidOperationException(
                "Segment rotation threshold must be positive and below the ingest maximum duration.");
    }

    private static RotationContract LoadContract()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Segment rotation policy resource is missing.");
        return JsonSerializer.Deserialize<RotationContract>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidOperationException("Segment rotation policy resource is empty.");
    }

    private sealed record RotationContract(
        int SchemaVersion,
        long MaxDurationMilliseconds,
        long RotateAfterMilliseconds);
}
