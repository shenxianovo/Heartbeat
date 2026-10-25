using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed record VerificationRequest(string Mode, string? Base, bool PlanOnly, bool Json, bool Quality = false)
{
    public async Task<VerificationRequest> ResolveAsync(IProcessRunner runner, CancellationToken cancellationToken)
    {
        var baseRef = await ResolveBaseAsync(Mode, Base, runner, cancellationToken);
        // Pin quality comparisons to one commit so both checks see the same base.
        if (Quality && baseRef is not null) baseRef = await ResolveCommitAsync(baseRef, runner, cancellationToken);
        return this with { Base = baseRef };
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

    private static async Task<string> ResolveCommitAsync(string baseRef, IProcessRunner runner, CancellationToken cancellationToken)
    {
        var resolved = await runner.CaptureAsync("git", ["rev-parse", "--verify", $"{baseRef}^{{commit}}"], null, cancellationToken);
        if (resolved.ExitCode != 0) throw new CommandUsageException($"Invalid Git base '{baseRef}'.");
        return resolved.StdOut.Trim();
    }
}

internal sealed record VerificationStep(string Name, string FileName, IReadOnlyList<string> Arguments);

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
        if (selection.HasFlag(CheckSelection.Model)) steps.Add(ModelTests(repository));
        if (selection.HasFlag(CheckSelection.Backend)) steps.Add(IntegrationTests(repository));
        if (selection.HasFlag(CheckSelection.Cli)) steps.Add(CliTests(repository));
        if (selection.HasFlag(CheckSelection.Frontend)) steps.Add(Frontend(repository));
        if (selection.HasFlag(CheckSelection.Docs)) steps.AddRange(Docs(repository));
        else if (selection.HasFlag(CheckSelection.DocsCheck)) steps.Add(DocsCheck(repository));
        return new("changed", request.Base, paths, steps);
    }

    // READMEs, AGENTS.md and agent skills are project documents covered by the docs consistency check.
    private static CheckSelection Select(string path)
    {
        var selection = SelectByArea(path);
        var name = Path.GetFileName(path);
        var isProjectDocument = name is "README.md" or "AGENTS.md"
            || (path.StartsWith(".agents/", StringComparison.Ordinal) && path.EndsWith(".md", StringComparison.Ordinal));
        return isProjectDocument ? (selection & ~CheckSelection.Unknown) | CheckSelection.DocsCheck : selection;
    }

    // The first matching area wins, so the OpenAPI contract precedes the rest of the docs site.
    private static readonly (string[] Prefixes, CheckSelection Selection)[] Areas =
    [
        (["src/Docs/Heartbeat.Docs/content/docs/api/openapi.json"],
            CheckSelection.Build | CheckSelection.Model | CheckSelection.Backend | CheckSelection.Docs),
        (["src/Docs/Heartbeat.Docs/"], CheckSelection.Docs),
        (["tools/Heartbeat.Dev/", "tests/Heartbeat.Dev.Tests/", ".agents/skills/verify-heartbeat/"],
            CheckSelection.Build | CheckSelection.Cli),
        (["tests/Heartbeat.Model.Tests/"], CheckSelection.Build | CheckSelection.Model),
        (["src/Core/", "src/Backend/", "tests/Heartbeat.Integration.Tests/", "tests/Heartbeat.Testing/"],
            CheckSelection.Build | CheckSelection.Model | CheckSelection.Backend),
        // Observer tests live in the integration test project.
        (["src/Observers/"], CheckSelection.Build | CheckSelection.Backend),
        (["src/Clients/Heartbeat.Web/"], CheckSelection.Frontend),
        (["tools/Heartbeat.AppHost/", "docker/nginx/"], CheckSelection.Build),
        ([".scratch/"], CheckSelection.None),
    ];

    private static CheckSelection SelectByArea(string path)
    {
        if (path is "README.md" or "AGENTS.md") return CheckSelection.None;
        foreach (var (prefixes, selection) in Areas)
        {
            if (prefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal))) return selection;
        }
        return CheckSelection.Unknown;
    }

    private static VerificationStep Build(RepositoryContext repository) =>
        new("dotnet-build", "dotnet", ["build", repository.Path("Heartbeat.slnx"), "--verbosity", "minimal"]);

    private static VerificationStep ModelTests(RepositoryContext repository) =>
        new("model-tests", "dotnet", ["test", "--project",
            repository.Path("tests", "Heartbeat.Model.Tests", "Heartbeat.Model.Tests.csproj")]);

    private static VerificationStep IntegrationTests(RepositoryContext repository) =>
        new("integration-tests", "dotnet", ["test", "--project",
            repository.Path("tests", "Heartbeat.Integration.Tests", "Heartbeat.Integration.Tests.csproj")]);

    private static VerificationStep CliTests(RepositoryContext repository) =>
        new("developer-cli-tests", "dotnet", ["test", "--project",
            repository.Path("tests", "Heartbeat.Dev.Tests", "Heartbeat.Dev.Tests.csproj")]);

    private static VerificationStep Frontend(RepositoryContext repository) =>
        new("frontend-verify", "npm", ["--prefix", repository.Path("src", "Clients", "Heartbeat.Web"), "run", "verify"]);

    private static VerificationStep DocsCheck(RepositoryContext repository) =>
        new("docs-check", "pnpm", ["--dir", repository.Path("src", "Docs", "Heartbeat.Docs"), "docs:check"]);

    private static IReadOnlyList<VerificationStep> Docs(RepositoryContext repository)
    {
        var root = repository.Path("src", "Docs", "Heartbeat.Docs");
        return [
            DocsCheck(repository),
            new("docs-test", "pnpm", ["--dir", root, "test"]),
            new("docs-types", "pnpm", ["--dir", root, "types:check"]),
            new("docs-build", "pnpm", ["--dir", root, "build"]),
        ];
    }

    private static IReadOnlyList<VerificationStep> Full(RepositoryContext repository) =>
        [Build(repository), ModelTests(repository), IntegrationTests(repository), CliTests(repository), Frontend(repository), .. Docs(repository)];

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
    private enum CheckSelection { None = 0, Build = 1, Backend = 2, Cli = 4, Docs = 8, Unknown = 16, Model = 32, DocsCheck = 64, Frontend = 128 }
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
