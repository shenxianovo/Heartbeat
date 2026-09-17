namespace Heartbeat.Dev;

internal sealed class EvidenceSession(ArtifactRun run)
{
    public ArtifactRun Run { get; } = run;
    public List<string> Commands { get; } = [];

    public static async Task<int> ExecuteAsync(
        RepositoryContext repository,
        string kind,
        string name,
        IReadOnlyList<string> limitations,
        Func<EvidenceSession, Task<int>> action,
        bool includesSensitiveEvidence = false,
        TextWriter? notes = null)
    {
        var store = new ArtifactStore(repository);
        var session = new EvidenceSession(store.Create(kind, name));
        var exitCode = 1;
        string? failure = null;
        try
        {
            exitCode = await action(session);
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            exitCode = 130;
            failure = "OperationCanceledException";
            throw;
        }
        catch (Exception exception)
        {
            // Exception messages can contain native/user context; record only the failure kind.
            failure = exception.GetType().Name;
            throw;
        }
        finally
        {
            var artifacts = Directory.EnumerateFileSystemEntries(session.Run.Directory)
                .Select(Path.GetFileName)
                .Where(item => item is not null and not "manifest.json")
                .Select(item => item!)
                .Order(StringComparer.Ordinal)
                .ToArray();
            await ArtifactStore.WriteManifestAsync(session.Run, new EvidenceManifest(
                session.Run.Id, kind, name, session.Run.CreatedAt, DateTimeOffset.UtcNow,
                exitCode, includesSensitiveEvidence, session.Commands, artifacts, limitations, failure),
                CancellationToken.None);
            await CollectAsync(store, notes);
        }
    }

    /// <summary>
    /// 跑完就收一次。手动 `artifacts prune` 的默认值以前基本命中不到，证据目录只会涨；
    /// 保留规则与 `artifacts prune` 完全一致（最近若干次 + 最近若干次失败的），并且把删了什么说出来。
    /// </summary>
    public static Task CollectAsync(RepositoryContext repository, TextWriter? notes) =>
        CollectAsync(new ArtifactStore(repository), notes);

    private static async Task CollectAsync(ArtifactStore store, TextWriter? notes)
    {
        try
        {
            var pruned = store.Prune(RetentionPolicy.Default);
            if (pruned.Candidates.Count == 0 || notes is null) return;
            await notes.WriteLineAsync(
                $"Pruned {pruned.Candidates.Count} stale evidence run(s), {LocalInventory.FormatBytes(pruned.Bytes)} "
                + $"(kept the newest {RetentionPolicy.Default.Keep}, the newest {RetentionPolicy.Default.KeepFailed} failed, "
                + $"and everything from the last {RetentionPolicy.Default.OlderThan.TotalDays:0} day(s)).");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (notes is not null) await notes.WriteLineAsync($"Could not prune old evidence: {exception.Message}");
        }
    }
}
