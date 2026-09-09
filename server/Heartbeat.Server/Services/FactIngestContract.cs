using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.Facts;

namespace Heartbeat.Server.Services;

public sealed class FactIngestException(string message, bool conflict = false) : ArgumentException(message)
{
    public bool IsConflict { get; } = conflict;
}

internal static class FactIngestContract
{
    internal static string Canonical(JsonElement value)
    {
        if (FactJson.Validate(value) is { } error)
            throw new FactIngestException(error);
        return FactJson.Canonicalize(value);
    }

    internal static void Snapshot(FactSnapshot fact, string kind, DateTimeOffset now)
    {
        if (fact.StreamId == Guid.Empty || fact.FactId == Guid.Empty || fact.FactId.Version != 7 || fact.Revision is <= 0 or > 9_007_199_254_740_991)
            throw new FactIngestException("Invalid Fact envelope.");
        if (fact.ObservedAt is { } observed && (observed.Offset != TimeSpan.Zero || observed > now.AddMinutes(5)))
            throw new FactIngestException("Invalid Fact observedAt.");
        if (kind == "segment")
        {
            if (fact.Start is not { } start || fact.End is not { } end || fact.IsFinal is null || fact.OccurredAt is not null ||
                start.Offset != TimeSpan.Zero || end.Offset != TimeSpan.Zero || start > end || end > now.AddMinutes(5))
                throw new FactIngestException("Segment requires a valid UTC start/end/isFinal interval.");
        }
        else if (fact.OccurredAt is not { } at || fact.Start is not null || fact.End is not null || fact.IsFinal is not null ||
            at.Offset != TimeSpan.Zero || at > now.AddMinutes(5))
            throw new FactIngestException("Event requires only a valid UTC occurredAt time.");
        if (fact.Payload is not { } payload) throw new FactIngestException("Fact requires payload.");
        if (FactJson.Validate(payload) is { } error) throw new FactIngestException(error);
    }

    internal static Guid LegacyId(string value) => Guid.ParseExact(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(value))), "N");

    internal static Guid ProjectedSegmentId(Guid streamId, Guid factId)
    {
        var identity = Encoding.ASCII.GetBytes($"{streamId:D}/{factId:D}");
        var value = (factId.ToString("N")[..12] + Convert.ToHexStringLower(SHA256.HashData(identity).AsSpan(0, 10))).ToCharArray();
        value[12] = '7';
        value[16] = "89ab"[Convert.ToInt32(value[16].ToString(), 16) & 3];
        return Guid.ParseExact(new string(value), "N");
    }
}
