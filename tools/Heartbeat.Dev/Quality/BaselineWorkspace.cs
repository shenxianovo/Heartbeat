namespace Heartbeat.Dev;

internal sealed record BaselineWorkspace(string Path, bool Reused)
{
    /// 标记依赖恢复和首次扫描成功，供后续扫描使用 `--no-restore`。
    private string RestoreMarker => System.IO.Path.Combine(Path, ".baseline-restored");

    public bool NeedsRestore => !File.Exists(RestoreMarker);

    public void MarkRestored() => File.WriteAllText(RestoreMarker, "restored");
}

/// <summary>
/// 按 commit 缓存独立基线工作树，复用 `obj/`、`bin/` 和 `node_modules`，减少重复准备。
/// 缓存位于 Git 忽略的 `.artifacts/quality-baselines/<commit>`，删除后可重建。
/// </summary>
internal sealed class BaselineWorkspaceCache(RepositoryContext repository, IProcessRunner runner)
{
    public string Root { get; } = repository.Path(".artifacts", "quality-baselines");

    /// 以解决方案文件判断工作树是否就绪；缺失时重建。
    private const string ReadyMarker = "Heartbeat.slnx";

    public async Task<(BaselineWorkspace? Workspace, string? Error)> PrepareAsync(
        string commit,
        ICollection<string> commands,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        // 用解析后的提交号作缓存键，避免 HEAD 或分支移动后复用旧基线。
        var resolved = await ResolveAsync(commit, cancellationToken);
        if (resolved is null)
        {
            return (null, $"Could not resolve Git base '{commit}' to a commit.");
        }
        var path = System.IO.Path.Combine(Root, resolved);
        if (File.Exists(System.IO.Path.Combine(path, ReadyMarker)))
        {
            commands.Add($"reuse cached baseline worktree .artifacts/quality-baselines/{resolved[..7]}");
            return (new BaselineWorkspace(path, true), null);
        }

        var added = await AddAsync(path, resolved, cancellationToken);
        if (added.ExitCode != 0)
        {
            // 清理上次失败的工作树登记和残留目录，再重试一次。
            await runner.CaptureAsync("git", ["worktree", "prune"], null, cancellationToken);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            added = await AddAsync(path, resolved, cancellationToken);
        }
        commands.Add($"git worktree add --detach .artifacts/quality-baselines/{resolved[..7]} {resolved[..7]}");
        return added.ExitCode == 0
            ? (new BaselineWorkspace(path, false), null)
            : (null, $"Could not materialize Git base '{resolved[..7]}': {LastUsefulLine(added)}");
    }

    private async Task<string?> ResolveAsync(string revision, CancellationToken cancellationToken)
    {
        var result = await runner.CaptureAsync(
            "git", ["rev-parse", "--verify", $"{revision}^{{commit}}"], null, cancellationToken);
        var candidate = result.StdOut.Trim();
        return result.ExitCode == 0 && candidate.Length == 40 ? candidate : null;
    }

    private Task<ProcessResult> AddAsync(string path, string commit, CancellationToken cancellationToken) =>
        runner.CaptureAsync("git", ["worktree", "add", "--detach", path, commit], null, cancellationToken);

    private static string LastUsefulLine(ProcessResult result) =>
        (result.StdErr + Environment.NewLine + result.StdOut)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?.Trim() ?? "command failed";
}
