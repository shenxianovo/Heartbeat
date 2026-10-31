using System.Net.Http;
using System.Text.Json;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observer.Runtime;

internal sealed partial class RuntimeHost
{
    private readonly RuntimeConnection _connection;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _syncStop = new();
    private Task? _sync;
    private readonly Dictionary<Guid, (DateTimeOffset At, string Json)> _reports = [];
    public bool Paired => _connection.Identity is not null;
    public Task<Guid> PairAsync(string code, string name, CancellationToken token) => _connection.PairAsync(code, name, token);
    public void StartSynchronization() => _sync ??= Task.Run(() => SynchronizeAsync(_syncStop.Token));

    private object Status(ManagedObserver observer)
    {
        var item = observer.Registration;
        return new { item.PackageId, item.Name, item.Version, item.InstanceId, item.Installed, item.Enabled, item.Removed, item.Settings,
            observer.State, observer.Error, observer.Diagnostic, appliedRevision = item.Revision, pendingChanges = item.Pending.Length,
            sync = new { item.Revision, pendingChanges = item.Pending.Length, error = item.SyncError } };
    }
    private async Task<InstalledObserver[]> SnapshotAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return _observers.Values.Select(observer => observer.Registration).ToArray(); }
        finally { _gate.Release(); }
    }
    private async Task ApplyAsync(InstanceConfiguration config, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!_observers.TryGetValue(config.EntityId, out var observer)) return;
            var before = observer.Registration;
            var after = before.Merge(config);
            if (ReferenceEquals(before, after)) return;
            _packages.Save(after); observer.SetRegistration(after);
            if (after.Removed || !after.Enabled || !JsonElement.DeepEquals(before.Settings, after.Settings)) await observer.StopAsync();
            observer.Start();
        }
        finally { _gate.Release(); }
    }
    private async Task SynchronizeAsync(CancellationToken token)
    {
        // Polling runs separately so installing a package does not make a connected host appear offline.
        var polling = PollAsync(token);
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_connection.CanConnect)
                    try
                    {
                        foreach (var item in await SnapshotAsync(token))
                        {
                            if (!item.Removed && item.SyncError is null)
                                await SynchronizeInstanceAsync(item, token);
                            await ReportAsync(item.InstanceId, token);
                        }
                    }
                    catch (Exception exception) when (exception is HttpRequestException or ManagementFailure or OperationCanceledException && !token.IsCancellationRequested)
                    { _connection.Offline(); }
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await polling; }
    }
    private async Task SynchronizeInstanceAsync(InstalledObserver item, CancellationToken token)
    {
        try
        {
            JsonElement response;
            if (item.Revision == 0)
                response = await _connection.SendAsync(HttpMethod.Put, $"runtime/instances/{item.InstanceId}", new {
                    instanceKey = item.PackageId, item.Name, item.Scope, item.Enabled, item.Settings, localSequence = item.NextSequence - 1 }, token);
            else if (item.Pending.FirstOrDefault() is { } change)
                response = await _connection.SendAsync(HttpMethod.Post, $"runtime/instances/{item.InstanceId}/changes", change, token);
            else return;
            await ApplyAsync(response.Deserialize<InstanceConfiguration>(RuntimeConnection.Json)!, token);
        }
        catch (ManagementFailure error)
        {
            await _gate.WaitAsync(token);
            try
            {
                var observer = _observers[item.InstanceId];
                var latest = observer.Registration;
                var removed = error.Code == "instance_removed";
                var updated = latest with { SyncError = error.Code, Removed = removed || latest.Removed, Changes = removed ? [] : latest.Pending };
                _packages.Save(updated); observer.SetRegistration(updated);
                if (removed) await observer.StopAsync();
            }
            finally { _gate.Release(); }
        }
    }
    private async Task ReportAsync(Guid id, CancellationToken token)
    {
        JsonElement status;
        await _gate.WaitAsync(token);
        try
        {
            var observer = _observers[id];
            if (observer.Registration.Revision == 0 && !observer.Registration.Removed) return;
            status = JsonLines.Json(Status(observer));
        }
        finally { _gate.Release(); }
        var now = DateTimeOffset.UtcNow;
        var json = status.GetRawText();
        if (_reports.TryGetValue(id, out var previous) && (now - previous.At < TimeSpan.FromSeconds(5)
            || previous.Json == json && now - previous.At < TimeSpan.FromSeconds(60))) return;
        await _connection.SendAsync(HttpMethod.Put, $"runtime/instances/{id}/status", status, token);
        _reports[id] = (now, json);
    }
    private async Task PollAsync(CancellationToken token)
    {
        var operations = new List<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!_connection.CanConnect) { await Task.Delay(TimeSpan.FromSeconds(1), token); continue; }
                try
                {
                    var instances = (await SnapshotAsync(token)).Where(item => item.Revision > 0).Select(item => new { entityId = item.InstanceId, item.Revision }).ToArray();
                    var response = await _connection.SendAsync(HttpMethod.Post, "runtime/poll", new { instances }, token);
                    _connection.Connected();
                    foreach (var config in response.GetProperty("instances").EnumerateArray())
                        await ApplyAsync(config.Deserialize<InstanceConfiguration>(RuntimeConnection.Json)!, token);
                    operations.RemoveAll(task => task.IsCompletedSuccessfully);
                    foreach (var operation in response.GetProperty("operations").EnumerateArray())
                        operations.Add(ExecuteOperationAsync(operation.Clone(), token));
                }
                catch (Exception exception) when (exception is HttpRequestException or ManagementFailure or OperationCanceledException && !token.IsCancellationRequested)
                { _connection.Offline(); await Task.Delay(TimeSpan.FromSeconds(2), token); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            try { await Task.WhenAll(operations); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }
    private async Task ExecuteOperationAsync(JsonElement operation, CancellationToken token)
    {
        object outcome;
        try
        {
            var action = operation.GetProperty("action").GetString()!;
            var id = operation.GetProperty("packageId").ValueKind == JsonValueKind.String ? operation.GetProperty("packageId").GetString() : null;
            Guid? expectedInstanceId = operation.GetProperty("entityId").ValueKind == JsonValueKind.String ? operation.GetProperty("entityId").GetGuid() : null;
            if (expectedInstanceId is { } target)
            {
                var instance = (await SnapshotAsync(token)).Single(item => item.InstanceId == target);
                if (instance.Removed) throw new InvalidOperationException("The target instance was removed.");
                id = instance.PackageId;
            }
            var result = await RequestAsync(action, JsonLines.Json(new { packageId = id, expectedInstanceId, newInstance = operation.GetProperty("newInstance").GetBoolean() }), token);
            outcome = new { outcome = "succeeded", result };
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        { outcome = new { outcome = "failed", error = exception.Message }; }
        while (!token.IsCancellationRequested && _connection.CanConnect)
        {
            try { await _connection.SendAsync(HttpMethod.Post, $"runtime/operations/{operation.GetProperty("operationId").GetGuid()}/result", outcome, token); return; }
            catch (ManagementFailure error) when (error.Code == "not_found") { return; }
            catch (Exception exception) when (exception is HttpRequestException or ManagementFailure or OperationCanceledException && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(2), token); }
        }
    }
}
