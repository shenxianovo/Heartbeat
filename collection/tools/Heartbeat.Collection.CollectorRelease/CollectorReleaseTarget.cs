namespace Heartbeat.Collection.CollectorRelease;

/// <summary>
/// A Collector that publishes itself on its own tag. The tag slug only selects which target to
/// build; the PackageId recorded here is checked against the built Collector Package manifest, so a
/// drifting slug fails the release instead of inventing a second Package identity.
///
/// The System Collector is intentionally absent: it uses BuiltIn Delivery and ships with the Desktop
/// application, so it has no Web release target.
/// </summary>
public sealed record CollectorReleaseTarget(
    string Slug,
    string PackageId,
    string ProjectPath,
    string ArtifactFileName)
{
    public static readonly CollectorReleaseTarget VRChat = new(
        "vrchat",
        "heartbeat.collector.vrchat",
        "collection/collectors/Heartbeat.Collector.VRChat/Heartbeat.Collector.VRChat.csproj",
        "vrchat.zip");

    public static IReadOnlyList<CollectorReleaseTarget> All { get; } = [VRChat];

    public static CollectorReleaseTarget? Find(string slug) =>
        All.FirstOrDefault(target => string.Equals(target.Slug, slug, StringComparison.Ordinal));
}
