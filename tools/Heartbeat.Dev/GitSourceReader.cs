namespace Heartbeat.Dev;

internal sealed class GitSourceReader(
    RepositoryContext repository,
    IProcessRunner runner)
{
    public async Task<SourceSnapshot> ReadWorktreeAsync(CancellationToken cancellationToken)
    {
        var listed = await runner.CaptureAsync(
            "git", ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], null, cancellationToken);
        EnsureSuccess(listed);
        var files = new List<SourceFileMetric>();
        foreach (var path in SplitNull(listed.StdOut))
        {
            if (!SourceCorpus.TryClassify(path, out var language, out var role)) continue;
            var absolute = repository.Path(path.Split('/'));
            if (!File.Exists(absolute)) continue;
            var text = await File.ReadAllTextAsync(absolute, cancellationToken);
            files.Add(new SourceFileMetric(path, language, role, SourceLineCounter.Count(text, language)));
        }
        return new SourceSnapshot("worktree", files);
    }

    public async Task<SourceSnapshot> ReadRevisionAsync(string revision, CancellationToken cancellationToken)
    {
        var resolved = await runner.CaptureAsync(
            "git", ["rev-parse", "--verify", $"{revision}^{{commit}}"], null, cancellationToken);
        if (resolved.ExitCode != 0)
        {
            throw new CommandUsageException($"Invalid Git base '{revision}'.");
        }
        var listed = await runner.CaptureAsync(
            "git", ["ls-tree", "-r", "--name-only", "-z", revision], null, cancellationToken);
        EnsureSuccess(listed);
        var files = new List<SourceFileMetric>();
        foreach (var path in SplitNull(listed.StdOut))
        {
            if (!SourceCorpus.TryClassify(path, out var language, out var role)) continue;
            var content = await runner.CaptureAsync("git", ["show", $"{revision}:{path}"], null, cancellationToken);
            EnsureSuccess(content);
            files.Add(new SourceFileMetric(path, language, role, SourceLineCounter.Count(content.StdOut, language)));
        }
        return new SourceSnapshot(resolved.StdOut.Trim(), files);
    }

    private static string[] SplitNull(string value) =>
        value.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static void EnsureSuccess(ProcessResult result)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StdErr.Trim());
    }
}
