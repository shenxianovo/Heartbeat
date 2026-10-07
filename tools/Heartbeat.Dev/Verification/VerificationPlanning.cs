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
        RepositoryContext repository, IProcessRunner runner, VerificationRequest request, CancellationToken cancellationToken)
    {
        if (request.Mode == "full") return new("full", request.Base, [], Full(repository));
        var paths = await ChangedPathsAsync(runner, request.Base!, cancellationToken);
        var selection = paths.Aggregate(CheckSelection.None, (current, path) => current | Select(path));
        if (selection.HasFlag(CheckSelection.Unknown)) return new("full-fallback", request.Base, paths, Full(repository));
        var steps = new List<VerificationStep>();
        if (selection.HasFlag(CheckSelection.Build)) steps.Add(Build(repository));
        if (selection.HasFlag(CheckSelection.Backend)) steps.Add(IntegrationTests(repository));
        if (selection.HasFlag(CheckSelection.Cli)) steps.Add(CliTests(repository));
        if (selection.HasFlag(CheckSelection.Docs)) steps.AddRange(Docs(repository));
        return new("changed", request.Base, paths, steps);
    }

    private static CheckSelection Select(string path)
    {
        if (path == "src/Docs/Heartbeat.Docs/content/docs/api/openapi.json")
            return CheckSelection.Build | CheckSelection.Backend | CheckSelection.Docs;
        if (path.StartsWith("src/Docs/Heartbeat.Docs/", StringComparison.Ordinal)) return CheckSelection.Docs;
        if (path.StartsWith("tools/Heartbeat.Dev/", StringComparison.Ordinal)
            || path.StartsWith("tests/Heartbeat.Dev.Tests/", StringComparison.Ordinal)
            || path.StartsWith(".agents/skills/verify-heartbeat/", StringComparison.Ordinal))
            return CheckSelection.Build | CheckSelection.Cli;
        if (path.StartsWith("src/Core/", StringComparison.Ordinal)
            || path.StartsWith("src/Backend/", StringComparison.Ordinal)
            || path.StartsWith("tests/Heartbeat.Integration.Tests/", StringComparison.Ordinal)
            || path.StartsWith("tests/Heartbeat.Testing/", StringComparison.Ordinal))
            return CheckSelection.Build | CheckSelection.Backend;
        if (path.StartsWith(".scratch/", StringComparison.Ordinal)
            || path.StartsWith(".agents/skills/disslopify/", StringComparison.Ordinal)
            || path is "README.md" or "AGENTS.md") return CheckSelection.None;
        return CheckSelection.Unknown;
    }

    private static VerificationStep Build(RepositoryContext repository) =>
        new("dotnet-build", "dotnet", ["build", repository.Path("Heartbeat.slnx"), "--verbosity", "minimal"]);

    private static VerificationStep IntegrationTests(RepositoryContext repository) =>
        new("integration-tests", "dotnet", ["test", "--project",
            repository.Path("tests", "Heartbeat.Integration.Tests", "Heartbeat.Integration.Tests.csproj")]);

    private static VerificationStep CliTests(RepositoryContext repository) =>
        new("developer-cli-tests", "dotnet", ["test", "--project",
            repository.Path("tests", "Heartbeat.Dev.Tests", "Heartbeat.Dev.Tests.csproj")]);

    private static IReadOnlyList<VerificationStep> Docs(RepositoryContext repository)
    {
        var root = repository.Path("src", "Docs", "Heartbeat.Docs");
        return [
            new("docs-types", "pnpm", ["--dir", root, "types:check"]),
            new("docs-build", "pnpm", ["--dir", root, "build"]),
        ];
    }

    private static IReadOnlyList<VerificationStep> Full(RepositoryContext repository) =>
        [Build(repository), IntegrationTests(repository), CliTests(repository), .. Docs(repository)];

    private static async Task<IReadOnlyList<string>> ChangedPathsAsync(
        IProcessRunner runner, string baseRef, CancellationToken cancellationToken)
    {
        var tracked = await runner.CaptureAsync("git",
            ["diff", "--no-renames", "--name-only", "-z", baseRef, "--"], null, cancellationToken);
        if (tracked.ExitCode != 0) throw new CommandUsageException($"Invalid Git base '{baseRef}': {tracked.StdErr.Trim()}");
        var untracked = await runner.CaptureAsync("git",
            ["ls-files", "--others", "--exclude-standard", "-z"], null, cancellationToken);
        if (untracked.ExitCode != 0) throw new InvalidOperationException(untracked.StdErr.Trim());
        return tracked.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Concat(untracked.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    [Flags]
    private enum CheckSelection { None = 0, Build = 1, Backend = 2, Cli = 4, Docs = 8, Unknown = 16 }
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
