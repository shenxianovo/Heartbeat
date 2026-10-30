using System.Text.Json;

namespace Orisumi.Dev;

internal sealed record ArtifactRun(string Id, string Directory, DateTimeOffset CreatedAt);

/// 一次运行在列表里显示的内容；没有 manifest 的运行还没结束或中途退出，退出码为 null。
internal sealed record RunSummary(ArtifactRun Run, string What, int? ExitCode, TimeSpan? Duration, IReadOnlyList<string> Failed);

internal sealed record EvidenceManifest(
    string RunId,
    string Kind,
    string Name,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int ExitCode,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Artifacts,
    IReadOnlyList<string> Limitations,
    string? Failure = null)
{
    public string Status => ExitCode switch
    {
        0 => "succeeded",
        130 => "cancelled",
        _ => "failed",
    };
}

/// <summary>
/// 证据保留规则。默认值必须在本项目的实际节奏下真的会命中——「保留最近 20 次且 14 天以上才删」
/// 在一天跑十几次的节奏下等于空操作，证据目录因此长到 2.7G。
/// 失败的运行单独保一批：出问题那次的产物才是最值得留的。
/// </summary>
internal sealed record RetentionPolicy(int Keep, int KeepFailed, TimeSpan OlderThan)
{
    public static readonly RetentionPolicy Default = new(10, 5, TimeSpan.FromDays(2));
}

internal sealed record PruneOutcome(IReadOnlyList<ArtifactRun> Candidates, long Bytes);

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

    /// <summary>
    /// 保留最近 <see cref="RetentionPolicy.Keep"/> 次，外加最近 <see cref="RetentionPolicy.KeepFailed"/> 次失败的运行；
    /// 其余超过保留期的都是候选。没有 manifest 的运行按「失败」对待——它是中途死掉的那种，值得留着看。
    /// </summary>
    public PruneOutcome SelectForPruning(RetentionPolicy policy)
    {
        var runs = List();
        var retained = new HashSet<string>(
            runs.Take(Math.Max(0, policy.Keep)).Select(run => run.Id), StringComparer.Ordinal);
        foreach (var failed in runs.Where(IsFailed).Take(Math.Max(0, policy.KeepFailed)))
        {
            retained.Add(failed.Id);
        }
        var cutoff = DateTimeOffset.UtcNow - policy.OlderThan;
        var candidates = runs
            .Where(run => !retained.Contains(run.Id) && run.CreatedAt < cutoff)
            .ToArray();
        return new PruneOutcome(candidates, candidates.Sum(Bytes));
    }

    public static RunSummary Summarize(ArtifactRun run)
    {
        var manifest = ReadJson(Path.Combine(run.Directory, "manifest.json"));
        var kind = Text(manifest, "kind");
        var name = Text(manifest, "name") ?? NameFromId(run.Id);
        var what = kind == "quality" ? "analyze quality" : $"verify {(name == "full-fallback" ? "full (fallback)" : name)}";
        if (kind != "quality" && File.Exists(Path.Combine(run.Directory, "quality.json"))) what += " + quality";
        int? exitCode = manifest is { } document && document.TryGetProperty("exitCode", out var code) ? code.GetInt32() : null;
        return new RunSummary(run, what, exitCode, Duration(manifest), FailedSteps(run));
    }

    private static TimeSpan? Duration(JsonElement? manifest) =>
        manifest is { } document
        && document.TryGetProperty("startedAt", out var started) && document.TryGetProperty("completedAt", out var completed)
            ? completed.GetDateTimeOffset() - started.GetDateTimeOffset() : null;

    /// 检查步骤按退出码判断，冒烟场景按状态判断；取消的步骤不算失败。
    private static string[] FailedSteps(ArtifactRun run) =>
        ReadJson(Path.Combine(run.Directory, "verification.json")) is { } verification
        && verification.TryGetProperty("results", out var results)
            ? [.. results.EnumerateArray().Where(IsFailedStep).Select(result => Text(result, "name") ?? "")]
            : [];

    private static bool IsFailedStep(JsonElement result) =>
        result.TryGetProperty("exitCode", out var code) ? code.GetInt32() is not (0 or 130) : Text(result, "status") == "failed";

    private static string NameFromId(string id) => id.Split('-') is [_, _, var name, ..] ? name : id;

    private static string? Text(JsonElement? element, string name) =>
        element is { } value && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static JsonElement? ReadJson(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// 退出码非 0 算失败；manifest 缺失或读不出来也算——这两种都是最需要留证据的情况。
    public bool IsFailed(ArtifactRun run)
    {
        var manifest = Path.Combine(run.Directory, "manifest.json");
        if (!File.Exists(manifest)) return true;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            return !document.RootElement.TryGetProperty("exitCode", out var exitCode)
                || exitCode.GetInt32() != 0;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static long Bytes(ArtifactRun run)
    {
        try
        {
            return new DirectoryInfo(run.Directory)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Exists ? file.Length : 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// 每次验证跑完自动收一次，靠的就是这个：删除按保留规则挑出来的运行目录。
    public PruneOutcome Prune(RetentionPolicy policy)
    {
        var outcome = SelectForPruning(policy);
        foreach (var candidate in outcome.Candidates)
        {
            Delete(candidate);
        }
        return outcome;
    }

    public static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }
        return $"{value:0.##} {suffixes[suffix]}";
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
