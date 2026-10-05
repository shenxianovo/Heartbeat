namespace Heartbeat.Hub;

/// <summary>In-process custody uses the same SQLite transaction as the HTTP endpoint.</summary>
public sealed class LocalHubSubmissionClient(RecordOutbox queue) : IHubSubmissionClient
{
    public Task SubmitAsync(HubSubmission submission, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { queue.Accept(submission); }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or QueueCapacityException)
        { throw new IOException("Hub persistent custody was not confirmed; retain the submitted snapshots.", exception); }
        return Task.CompletedTask;
    }
}
