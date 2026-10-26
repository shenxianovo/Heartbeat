namespace Orisumi.Core;

public sealed record Observation(
    VersionId ObserverVersionId,
    VersionId ContentVersionId,
    VersionId? InputSetVersionId,
    DateTimeOffset? StartAt,
    DateTimeOffset? EndAt,
    string? TimeZone) : VersionContent
{
    public override string Category => "observation";
}
