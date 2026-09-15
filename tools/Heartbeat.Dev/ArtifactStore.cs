using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed record ArtifactRun(string Id, string Directory, DateTimeOffset CreatedAt);

internal sealed record EvidenceManifest(
    string RunId,
    string Kind,
    string Name,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int ExitCode,
    bool IncludesSensitiveEvidence,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Artifacts,
    IReadOnlyList<string> Limitations);

internal sealed class ArtifactStore(RepositoryContext repository)
{
    public string Root { get; } = repository.Path(".artifacts", "verification");

    public ArtifactRun Create(string kind, string name)
    {
        Directory.CreateDirectory(Root);
        var now = DateTimeOffset.UtcNow;
        var safeName = string.Concat(name.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'));
        var candidate = $"{now:yyyyMMddTHHmmssZ}-{kind}-{safeName}-{Guid.NewGuid():N}";
        var id = candidate[..Math.Min(80, candidate.Length)];
        var directory = Path.Combine(Root, id);
        Directory.CreateDirectory(directory);
        return new ArtifactRun(id, directory, now);
    }

    public static async Task WriteManifestAsync(
        ArtifactRun run,
        EvidenceManifest manifest,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(run.Directory, "manifest.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(manifest, JsonOptions.Indented) + Environment.NewLine,
            cancellationToken);
    }

    public IReadOnlyList<ArtifactRun> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }
        return Directory.EnumerateDirectories(Root)
            .Select(path => new DirectoryInfo(path))
            .Select(info => new ArtifactRun(info.Name, info.FullName, info.CreationTimeUtc))
            .OrderByDescending(run => run.CreatedAt)
            .ToArray();
    }

    public IReadOnlyList<ArtifactRun> SelectForPruning(int keep, TimeSpan olderThan)
    {
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        return List()
            .Skip(Math.Max(0, keep))
            .Where(run => run.CreatedAt < cutoff)
            .ToArray();
    }

    public void Delete(ArtifactRun run)
    {
        var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(run.Directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(root, comparison) || target.Equals(root.TrimEnd(Path.DirectorySeparatorChar), comparison))
        {
            throw new InvalidOperationException($"Refusing to prune path outside the artifact root: {target}");
        }
        Directory.Delete(target, recursive: true);
    }
}
