namespace Heartbeat.Dev;

internal sealed record BaselineWorkspace(string Path, bool Reused)
{
    /// 基线树里恢复过依赖、成功扫过一次的标记。有它才敢 `--no-restore`。
    private string RestoreMarker => System.IO.Path.Combine(Path, ".baseline-restored");

    public bool NeedsRestore => !File.Exists(RestoreMarker);

    public void MarkRestored() => File.WriteAllText(RestoreMarker, "restored");
}

/// <summary>
/// 基线必须在一棵干净的树上量，但每次 `git worktree add` + `restore` + `npm ci` 是跨基点度量最脆的一段。
/// 这里按 commit 缓存基线工作树：命中就复用，连带复用它的 `obj/`、`bin/` 与 `node_modules`。
/// 缓存放在 `.artifacts/quality-baselines/<commit>`（已 gitignore），删掉只会让下一次慢一点。
/// </summary>
internal sealed class BaselineWorkspaceCache(RepositoryContext repository, IProcessRunner runner)
{
    public string Root { get; } = repository.Path(".artifacts", "quality-baselines");

    /// 就绪标志：worktree 加出来之后一定有解决方案文件。半途失败的目录不会有它，会被重建。
    private const string ReadyMarker = "Heartbeat.slnx";

    public async Task<(BaselineWorkspace? Workspace, string? Error)> PrepareAsync(
        string commit,
        ICollection<string> commands,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        // 缓存键必须是提交号。`HEAD`、分支名这类会移动的引用一旦当成键，HEAD 前进之后还会命中
        // 同一个目录，量出来的基线就是旧的——一个安静地说假话的闸门。
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
            // 上一次跑到一半留下的目录会挡住 add。清掉登记与目录再试一次，还不行才报错。
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
