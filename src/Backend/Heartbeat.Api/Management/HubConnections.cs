using System.Collections.Concurrent;
using Heartbeat.Management;

namespace Heartbeat.Api.Management;

// Live management and activity only. API restart replays neither operations nor animations.
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
            if (connection.Session != checkIn.SessionId) connection.Activity = null;
            connection.Session = checkIn.SessionId;
            connection.LastSeen = now;
            connection.Report = checkIn.Report;
            if (checkIn.Result is { } result && connection.Command?.Id == result.Id)
                connection.Completion?.TrySetResult(result);
            return connection.TakeCommand(now);
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

    public bool ReportActivity(Guid owner, Guid hub, HubActivityReport report)
    {
        if (!_connections.TryGetValue((owner, hub), out var connection)) return false;
        lock (connection)
        {
            if (connection.Session != report.SessionId ||
                connection.LastSeen <= clock.GetUtcNow() - HubManagement.OnlineTimeout) return false;
            if (connection.Activity is { } previous && !Advances(previous, report.Activity)) return false;
            connection.Activity = report.Activity;
            connection.ActivitySeen = clock.GetUtcNow();
            return true;
        }
    }

    private static bool Advances(DeliveryActivitySnapshot previous, DeliveryActivitySnapshot next) =>
        next.Epoch == previous.Epoch && next.CapturedAt >= previous.CapturedAt;

    public IReadOnlyDictionary<Guid, DeliveryActivitySnapshot> GetActivities(Guid owner)
    {
        var result = new Dictionary<Guid, DeliveryActivitySnapshot>();
        var now = clock.GetUtcNow();
        foreach (var (key, connection) in _connections)
        {
            if (key.Owner != owner) continue;
            lock (connection)
            {
                if (connection.LastSeen > now - HubManagement.OnlineTimeout &&
                    connection.ActivitySeen > now - HubManagement.ActivityTimeout && connection.Activity is { } activity)
                    result.Add(key.Hub, activity);
            }
        }
        return result;
    }

    public void Retire(Guid owner, Guid hub)
    {
        if (_connections.TryRemove((owner, hub), out var connection))
            lock (connection) connection.Completion?.TrySetException(new InvalidOperationException("Hub was retired."));
    }

    private sealed class Connection
    {
        public HubCommand? TakeCommand(DateTimeOffset now)
        {
            if (Command is null || Delivered || Command.ExpiresAt <= now) return null;
            Delivered = true;
            return Command;
        }

        public HubReport? Report;
        public DeliveryActivitySnapshot? Activity;
        public DateTimeOffset ActivitySeen;
        public Guid Session;
        public DateTimeOffset LastSeen;
        public HubCommand? Command;
        public bool Delivered;
        public TaskCompletionSource<HubCommandResult>? Completion;
    }
}
