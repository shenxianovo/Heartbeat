using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed record VerificationRequest(string Mode, string? Base, bool PlanOnly, bool Json)
{
    public static async Task<VerificationRequest> ParseAsync(
        RepositoryContext repository,
        IProcessRunner runner,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (args[0] is not ("changed" or "full"))
        {
            throw new CommandUsageException($"Unknown verify mode '{args[0]}'.");
        }
        var parsed = ParseOptions(args);
        var baseRef = await ResolveBaseAsync(args[0], parsed.Base, runner, cancellationToken);
        _ = repository;
        return new VerificationRequest(args[0], baseRef, parsed.Plan, parsed.Json);
    }

    private static ParsedVerificationOptions ParseOptions(IReadOnlyList<string> args)
    {
        string? baseRef = null;
        var plan = false;
        var json = false;
        for (var index = 1; index < args.Count; index++)
        {
            if (args[index] == "--base")
            {
                if (++index >= args.Count) throw new CommandUsageException("Missing value for --base.");
                baseRef = args[index];
            }
            else if (args[index] == "--plan") plan = true;
            else if (args[index] == "--json") json = true;
            else throw new CommandUsageException($"Unknown verify option '{args[index]}'.");
        }
        return new ParsedVerificationOptions(baseRef, plan, json);
    }

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

    private sealed record ParsedVerificationOptions(string? Base, bool Plan, bool Json);
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
        if (path.StartsWith("docs/", StringComparison.Ordinal)
            || path is "README.md" or "AGENTS.md") return CheckSelection.None;
        return CheckSelection.Unknown;
    }

    private static bool IsCli(string path) =>
        path.StartsWith("tools/Heartbeat.Dev/", StringComparison.Ordinal)
        || path.StartsWith("tests/Heartbeat.Dev.Tests/", StringComparison.Ordinal)
        || path.StartsWith(".agents/skills/verify-heartbeat/", StringComparison.Ordinal);

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
        var tracked = await runner.CaptureAsync("git", ["diff", "--name-only", "-z", baseRef, "--"], null, cancellationToken);
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
