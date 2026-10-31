using System.Text.Json;

namespace Orisumi.Core;

public sealed record Observation(
    EntityId ObserverId,
    VersionId? ObserverVersionId,
    string Formation,
    JsonElement Fields,
    VersionId? InputSetVersionId = null,
    DateTimeOffset? StartAt = null,
    DateTimeOffset? EndAt = null,
    string? TimeZone = null) : VersionContent
{
    public override string Category => "observation";
}
