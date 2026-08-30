using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Server.Services;

public enum SegmentIngestContractViolation
{
    LegacyAppName,
    MissingSystemAppIdentity,
    MalformedAppIdentity,
    InvalidSegment,
    IdentityConflict
}

public sealed class SegmentIngestContractException(
    SegmentIngestContractViolation violation,
    string message,
    Exception? innerException = null) : ArgumentException(message, innerException)
{
    public SegmentIngestContractViolation Violation { get; } = violation;
}

/// <summary>
/// Strict segment boundary shared by HTTP ingress and internal callers. Validation must run before
/// resolving a Device or querying segment/App facts so a rejected batch has no database effects.
/// </summary>
public static class SegmentIngestContract
{
    public static void Validate(
        IReadOnlyCollection<ActivitySegmentItem> segments,
        DateTimeOffset? evaluatedAt = null)
    {
        foreach (var segment in segments)
        {
            if (segment.AppName is not null)
            {
                throw new SegmentIngestContractException(
                    SegmentIngestContractViolation.LegacyAppName,
                    "Legacy segment AppName is no longer accepted. Update Heartbeat to migrate its local cache.");
            }

            if (segment.Source == ActivitySources.System && string.IsNullOrWhiteSpace(segment.AppIdentityKey))
            {
                throw new SegmentIngestContractException(
                    SegmentIngestContractViolation.MissingSystemAppIdentity,
                    "System segments require AppIdentityKey.");
            }

            if (string.IsNullOrWhiteSpace(segment.AppIdentityKey))
                continue;

            try
            {
                _ = AppIdentityKeys.Normalize(segment.AppIdentityKey);
            }
            catch (ArgumentException ex)
            {
                throw new SegmentIngestContractException(
                    SegmentIngestContractViolation.MalformedAppIdentity,
                    ex.Message,
                    ex);
            }
        }

        var now = evaluatedAt ?? DateTimeOffset.UtcNow;
        if (segments.Any(segment => !SegmentValidationPolicy.IsValid(segment, now)))
        {
            throw new SegmentIngestContractException(
                SegmentIngestContractViolation.InvalidSegment,
                "Segment batch contains an invalid Id, identity, source, or time range.");
        }
    }
}
