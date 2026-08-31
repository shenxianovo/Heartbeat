using System.Text.RegularExpressions;

namespace Heartbeat.Collection.CollectorRelease;

/// <summary>
/// The explicit release trigger: <c>collector-{slug}/v{semver}</c>. Nothing else publishes a
/// user-visible candidate — an ordinary main build is a dry run.
/// </summary>
public sealed record CollectorReleaseTag(string Slug, string Version)
{
    private static readonly Regex Pattern = new(
        "^collector-(?<slug>[a-z][a-z0-9]*(?:-[a-z0-9]+)*)/v(?<version>[^/]+)$",
        RegexOptions.CultureInvariant);

    public static CollectorReleaseTag? Parse(string? tag)
    {
        if (tag is null)
            return null;
        var match = Pattern.Match(tag);
        return match.Success
            ? new CollectorReleaseTag(match.Groups["slug"].Value, match.Groups["version"].Value)
            : null;
    }

    public override string ToString() => $"collector-{Slug}/v{Version}";
}
