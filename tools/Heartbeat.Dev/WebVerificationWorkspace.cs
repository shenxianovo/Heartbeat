namespace Heartbeat.Dev;

// Next writes both build outputs and TypeScript configuration. Keep all writable
// project files private to the run, sharing only the installed dependencies.
internal static class WebVerificationWorkspace
{
    public static IReadOnlyDictionary<string, string?> Environment(
        RepositoryContext repository, IReadOnlyDictionary<string, string?>? existing = null)
    {
        var environment = existing is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(existing);
        environment["HEARTBEAT_VERIFICATION_ROOT"] = repository.Root;
        return environment;
    }

    public static async Task<string> PrepareAsync(RepositoryContext repository, ArtifactRun run, CancellationToken cancellationToken)
    {
        var source = repository.Path("src", "Frontend", "Heartbeat.Web");
        var target = Path.Combine(run.Directory, "web-workspace");
        if (Directory.Exists(target)) return target;
        CopySources(source, target);
        var dependencies = Path.Combine(source, "node_modules");
        if (!Directory.Exists(dependencies))
            throw new DirectoryNotFoundException($"Run npm --prefix {source} ci before browser verification.");
        var link = Path.Combine(target, "node_modules");
        if (OperatingSystem.IsWindows())
        {
            var result = await ProcessRunner.CaptureAsync(repository.Root, "node",
                ["-e", "require('node:fs').symlinkSync(process.argv[1], process.argv[2], 'junction')", dependencies, link],
                cancellationToken);
            if (result.ExitCode != 0) throw new IOException($"Cannot link browser dependencies: {result.StdErr}");
        }
        else Directory.CreateSymbolicLink(link, dependencies);
        return target;
    }

    private static void CopySources(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (entry.Name is "node_modules" or ".next" or "coverage" or "test-results" or "playwright-report"
                || entry.Name.StartsWith(".env", StringComparison.Ordinal)
                || entry.Name.EndsWith(".tsbuildinfo", StringComparison.Ordinal)
                || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var destination = Path.Combine(target, entry.Name);
            if (entry is DirectoryInfo directory) CopySources(directory.FullName, destination);
            else File.Copy(entry.FullName, destination);
        }
    }

    public static async Task<IReadOnlyList<string>> ArgumentsAsync(
        RepositoryContext repository, ArtifactRun run, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var web = repository.Path("src", "Frontend", "Heartbeat.Web");
        if (!arguments.Contains(web, StringComparer.Ordinal)) return arguments;
        var workspace = await PrepareAsync(repository, run, cancellationToken);
        return arguments.Select(value => value == web ? workspace : value).ToArray();
    }
}
