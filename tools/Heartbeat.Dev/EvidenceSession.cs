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
        bool includesSensitiveEvidence = false)
    {
        var session = new EvidenceSession(new ArtifactStore(repository).Create(kind, name));
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
        }
    }
}
