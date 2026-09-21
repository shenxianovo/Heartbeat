using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed record VerificationRequest(string Mode, string? Base, bool PlanOnly, bool Json)
{
    public async Task<VerificationRequest> ResolveAsync(IProcessRunner runner, CancellationToken cancellationToken) =>
        this with { Base = await ResolveBaseAsync(Mode, Base, runner, cancellationToken) };

    private static async Task<string?> ResolveBaseAsync(
        string mode,
        string? baseRef,
        IProcessRunner runner,
        CancellationToken cancellationToken)
    {
        if (mode != "changed" || baseRef is not null) return baseRef;
        var status = await runner.CaptureAsync("git", ["status", "--porcelain"], null, cancellationToken);
        if (status.ExitCode != 0) throw new InvalidOperationException(status.StdErr.Trim());
        if (string.IsNullOrWhiteSpace(status.StdOut))
            throw new CommandUsageException("verify changed needs --base when the worktree is clean.");
        return "HEAD";
    }

}

internal sealed record VerificationStep(
    string Name,
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?>? Environment = null);

internal sealed record VerificationPlan(string Mode, string? Base, IReadOnlyList<string> ChangedPaths, IReadOnlyList<VerificationStep> Steps);

internal static class VerificationPlanner
{
    public static async Task<VerificationPlan> CreateAsync(
        RepositoryContext repository,
        IProcessRunner runner,
        VerificationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Mode == "full")
        {
            return new VerificationPlan("full", request.Base, [], Full(repository));
        }

        var paths = await ChangedPathsAsync(runner, request.Base!, cancellationToken);
        var selection = paths.Aggregate(CheckSelection.None, (current, path) => current | Select(path));
        if (selection.HasFlag(CheckSelection.Unknown))
            return new VerificationPlan("full-fallback", request.Base, paths, Full(repository));

        var steps = new List<VerificationStep>();
        AddIf(steps, selection, CheckSelection.Dotnet, DotnetSolution(repository));
        AddIf(steps, selection, CheckSelection.Cli, DotnetCli(repository));
        AddIf(steps, selection, CheckSelection.Web, WebVerify(repository));
        AddIf(steps, selection, CheckSelection.Browser, WebBrowser(repository));
        return new VerificationPlan("changed", request.Base, paths, steps);
    }

    private static CheckSelection Select(string path)
    {
        if (IsCli(path)) return CheckSelection.Cli;
        if (path.StartsWith("src/Frontend/Heartbeat.Web/", StringComparison.Ordinal))
            return CheckSelection.Web | (NeedsBrowser(path) ? CheckSelection.Browser : CheckSelection.None);
        if (path.StartsWith("src/", StringComparison.Ordinal)
            || path.StartsWith("tests/Heartbeat.", StringComparison.Ordinal)) return CheckSelection.Dotnet;
        if (path is "global.json" or "Directory.Build.props" or "Directory.Packages.props")
            return CheckSelection.Dotnet;
        if (IsContract(path)) return CheckSelection.Dotnet;
        if (path.StartsWith("docs/", StringComparison.Ordinal)
            || path.StartsWith(".scratch/", StringComparison.Ordinal)
            || path is "README.md" or "AGENTS.md" or "CONTEXT.md") return CheckSelection.None;
        return CheckSelection.Unknown;
    }

    /// <summary>
    /// 契约文档不是散文：协议、录制 API 与存储模型写的是后端与采集端都要遵守的语义。
    /// 改了它至少要把 .NET 测试跑一遍，否则「文档改了、实现没改」这类偏差没有任何检查会发现。
    /// </summary>
    private static bool IsContract(string path) =>
        path.StartsWith("docs/protocols/", StringComparison.Ordinal)
        || path is "docs/recording-api.md" or "docs/recording-storage-model.md" or "docs/hub-record-delivery.md";

    private static bool IsCli(string path) =>
        path.StartsWith("tools/Heartbeat.Dev/", StringComparison.Ordinal)
        || path.StartsWith("tests/Heartbeat.Dev.Tests/", StringComparison.Ordinal)
        || path.StartsWith(".agents/skills/verify-heartbeat/", StringComparison.Ordinal)
        // 验证口径的说明与实现必须一起对：改了它就把 CLI 测试跑一遍。
        // Deleted launchers can still appear when comparing against an older Git base.
        || path.StartsWith("scripts/", StringComparison.Ordinal)
        || path is "docs/verification.md";

    private static bool NeedsBrowser(string path) =>
        path.Contains("/src/app/", StringComparison.Ordinal)
        || path.Contains("/src/components/", StringComparison.Ordinal)
        || path.Contains("/src/api/", StringComparison.Ordinal)
        || path.Contains("/tests/e2e/", StringComparison.Ordinal);

    private static void AddIf(
        List<VerificationStep> steps,
        CheckSelection selection,
        CheckSelection required,
        VerificationStep step)
    {
        if (selection.HasFlag(required)) steps.Add(step);
    }

    private static IReadOnlyList<VerificationStep> Full(RepositoryContext repository) =>
        [DotnetSolution(repository), WebVerify(repository), WebBrowser(repository)];

    private static VerificationStep DotnetSolution(RepositoryContext repository) =>
        new("dotnet", "dotnet", ["test", repository.Path("Heartbeat.slnx"), "--no-restore", "--verbosity", "minimal"]);

    private static VerificationStep DotnetCli(RepositoryContext repository) =>
        new("developer-cli", "dotnet", ["test", repository.Path("tests", "Heartbeat.Dev.Tests", "Heartbeat.Dev.Tests.csproj"), "--no-restore", "--verbosity", "minimal"]);

    private static VerificationStep WebVerify(RepositoryContext repository) =>
        new("web", "npm", ["--prefix", repository.Path("src", "Frontend", "Heartbeat.Web"), "run", "verify"]);

    private static VerificationStep WebBrowser(RepositoryContext repository) =>
        new("browser", "npm", ["--prefix", repository.Path("src", "Frontend", "Heartbeat.Web"), "run", "test:e2e"]);

    private static async Task<IReadOnlyList<string>> ChangedPathsAsync(
        IProcessRunner runner,
        string baseRef,
        CancellationToken cancellationToken)
    {
        var tracked = await runner.CaptureAsync("git", ["diff", "--no-renames", "--name-only", "-z", baseRef, "--"], null, cancellationToken);
        if (tracked.ExitCode != 0) throw new CommandUsageException($"Invalid Git base '{baseRef}': {tracked.StdErr.Trim()}");
        var untracked = await runner.CaptureAsync("git", ["ls-files", "--others", "--exclude-standard", "-z"], null, cancellationToken);
        if (untracked.ExitCode != 0) throw new InvalidOperationException(untracked.StdErr.Trim());
        return tracked.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Concat(untracked.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    [Flags]
    private enum CheckSelection
    {
        None = 0,
        Dotnet = 1,
        Cli = 2,
        Web = 4,
        Browser = 8,
        Unknown = 16,
    }
}

internal static class VerificationReporter
{
    public static async Task WriteAsync(TextWriter output, VerificationPlan plan, bool json)
    {
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                plan.Mode,
                plan.Base,
                plan.ChangedPaths,
                steps = plan.Steps.Select(step => new { step.Name, step.FileName, step.Arguments }),
            }, JsonOptions.Indented));
            return;
        }
        await output.WriteLineAsync($"Verification plan: {plan.Mode}");
        foreach (var step in plan.Steps)
        {
            await output.WriteLineAsync($"  {step.Name}: {step.FileName} {string.Join(' ', step.Arguments)}");
        }
        if (plan.Steps.Count == 0) await output.WriteLineAsync("  No executable checks selected.");
    }
}
