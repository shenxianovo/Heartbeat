using System.Collections.Concurrent;
using Heartbeat.Management;

namespace Heartbeat.Api.Management;

// Only live operations are kept here. Restarting the API never replays a command.
public sealed class HubConnections(TimeProvider clock)
{
    private readonly ConcurrentDictionary<(Guid Owner, Guid Hub), Connection> _connections = new();

    public HubCommand? CheckIn(Guid owner, Guid hub, HubCheckIn checkIn)
    {
        var connection = _connections.GetOrAdd((owner, hub), _ => new Connection());
        lock (connection)
        {
            var now = clock.GetUtcNow();
            if (connection.Session != checkIn.SessionId && connection.LastSeen > now - HubManagement.OnlineTimeout)
                throw new InvalidOperationException("Another instance is already using this Hub identity.");
            connection.Session = checkIn.SessionId;
            connection.LastSeen = now;
            connection.Report = checkIn.Report;
            if (checkIn.Result is { } result && connection.Command?.Id == result.Id)
                connection.Completion?.TrySetResult(result);
            var command = connection.Command;
            if (command is null || connection.Delivered || command.ExpiresAt <= now) return null;
            connection.Delivered = true;
            return command;
        }
    }

    public async Task<HubCommandResult> ExecuteAsync(Guid owner, Guid hub, CollectorOperation operation,
        CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue((owner, hub), out var connection))
            throw new InvalidOperationException("Hub is offline. No operation was queued.");
        TaskCompletionSource<HubCommandResult> completion;
        lock (connection)
        {
            if (connection.LastSeen <= clock.GetUtcNow() - HubManagement.OnlineTimeout)
                throw new InvalidOperationException("Hub is offline. No operation was queued.");
            if (connection.Command is not null)
                throw new InvalidOperationException("Another operation is in progress. Wait for its result.");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Completion = completion;
            connection.Delivered = false;
            connection.Command = new(Guid.NewGuid(), clock.GetUtcNow() + HubManagement.OperationTimeout, operation);
        }
        try { return await completion.Task.WaitAsync(HubManagement.OperationTimeout, cancellationToken); }
        finally
        {
            lock (connection) { connection.Command = null; connection.Completion = null; }
        }
    }

    public HubReport? GetReport(Guid owner, Guid hub)
    {
        if (!_connections.TryGetValue((owner, hub), out var connection)) return null;
        lock (connection)
        {
            if (connection.LastSeen <= clock.GetUtcNow() - HubManagement.OnlineTimeout) connection.Report = null;
            return connection.Report;
        }
    }

    public void Retire(Guid owner, Guid hub)
    {
        if (_connections.TryRemove((owner, hub), out var connection))
            lock (connection) connection.Completion?.TrySetException(new InvalidOperationException("Hub was retired."));
    }

    private sealed class Connection
    {
        public HubReport? Report;
        public Guid Session;
        public DateTimeOffset LastSeen;
        public HubCommand? Command;
        public bool Delivered;
        public TaskCompletionSource<HubCommandResult>? Completion;
    }
}
