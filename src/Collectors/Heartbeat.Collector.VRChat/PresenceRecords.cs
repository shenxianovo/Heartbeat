using Heartbeat.Contracts;
using System.Text.Json;
using Heartbeat.Hub;

namespace Heartbeat.Collector.VRChat;

internal sealed record VRChatRecord(string Type, RecordSnapshot Record);

// A projection for one uninterrupted connection. Names are frozen when a Range starts.
internal sealed class PresenceRecords(string target)
{
    private readonly Dictionary<string, VRChatPresenceUpdate> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecordSnapshot> _encounters = new(StringComparer.Ordinal);
    private RecordSnapshot? _visit;
    private VRChatLocation? _location;
    private static readonly TimeSpan MaximumGap = TimeSpan.FromMinutes(6);

    public IReadOnlyList<VRChatRecord> Observe(VRChatFeedItem item, string? worldName)
    {
        List<VRChatRecord> result = [];
        if (item.Event is { } update)
        {
            _events[update.AccountId] = update.At;
            Apply(update, worldName, result);
        }
        else if (item.Snapshot is { } snapshot) ApplySnapshot(snapshot, worldName, result);
        return result;
    }

    public string? WorldToResolve(VRChatFeedItem item) => item.Event?.AccountId == target
        ? item.Event.Location?.WorldId : item.Snapshot?.Users.FirstOrDefault(user => user.AccountId == target)?.Location?.WorldId;

    private void ApplySnapshot(VRChatPresenceSnapshot snapshot, string? worldName, List<VRChatRecord> result)
    {
        var own = snapshot.Users.First(user => user.AccountId == target);
        if (_users.TryGetValue(target, out var previous) && own.At - previous.At > MaximumGap) Break();
        var present = snapshot.Users.Select(user => user.AccountId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _users.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            if (_events.GetValueOrDefault(id) >= snapshot.RequestedAt) continue;
            _users.Remove(id);
            _encounters.Remove(id); // Absent is unknown, not an observed departure.
        }
        var applicable = snapshot.Users.Where(user => _events.GetValueOrDefault(user.AccountId) < snapshot.RequestedAt).ToArray();
        foreach (var friend in applicable.Where(user => user.AccountId != target)) _users[friend.AccountId] = friend;
        if (applicable.Any(user => user.AccountId == target)) Apply(own, worldName, result);
        else foreach (var friend in applicable) ApplyFriend(friend, result);
    }

    private void Apply(VRChatPresenceUpdate update, string? worldName, List<VRChatRecord> result)
    {
        if (_users.TryGetValue(update.AccountId, out var previous) && update.At < previous.At) return;
        if (update.AccountId == target && _users.TryGetValue(target, out var owner) && update.At - owner.At > MaximumGap)
            Break();
        _users[update.AccountId] = update;
        if (update.AccountId == target) ApplyOwner(update, worldName, result);
        else ApplyFriend(update, result);
    }

    private void ApplyOwner(VRChatPresenceUpdate update, string? worldName, List<VRChatRecord> result)
    {
        var renamed = _visit is not null && (
            NameChanged(_visit, "account", update.DisplayName) || NameChanged(_visit, "world", worldName));
        if (_location != update.Location || renamed)
        {
            EndVisit(update.At, update.Cause != "snapshot", result);
            _location = update.Location;
        }
        if (_location is null) return;
        _visit ??= NewRecord(update.At, new { instance_id = _location.InstanceId, basis = "api_visible" },
            new("account", "vrchat.account", target, update.DisplayName),
            new("world", "vrchat.world", _location.WorldId, worldName));
        _visit = _visit with { EndedAt = update.At };
        result.Add(new("vrchat.location", _visit));
        foreach (var friend in _users.Values.Where(user => user.AccountId != target))
            ReconcileFriend(friend, update.At > friend.At ? update.At : friend.At, false, result);
    }

    private void ApplyFriend(VRChatPresenceUpdate update, List<VRChatRecord> result)
    {
        if (!_users.TryGetValue(target, out var owner) || update.At - owner.At > MaximumGap) return;
        ReconcileFriend(update, update.At > owner.At ? update.At : owner.At, update.Cause != "snapshot", result);
    }

    private void ReconcileFriend(VRChatPresenceUpdate friend, DateTimeOffset at, bool eventBoundary, List<VRChatRecord> result)
    {
        var same = _location is not null && friend.Location == _location && at - friend.At <= MaximumGap;
        if (!same)
        {
            if (_encounters.Remove(friend.AccountId, out var ended) && eventBoundary)
                result.Add(new("vrchat.encounter", ended with { EndedAt = at }));
            return;
        }
        EndRenamedEncounter(friend, at, result);
        if (!_encounters.TryGetValue(friend.AccountId, out var current))
            current = NewRecord(at, new { instance_id = _location!.InstanceId, basis = "api_visible" },
                new("account", "vrchat.account", target, _users[target].DisplayName),
                new("friend", "vrchat.account", friend.AccountId, friend.DisplayName),
                new("world", "vrchat.world", _location.WorldId,
                    _visit?.Objects.FirstOrDefault(item => item.Role == "world")?.Name));
        current = current with { EndedAt = at };
        _encounters[friend.AccountId] = current;
        result.Add(new("vrchat.encounter", current));
    }

    private void EndRenamedEncounter(VRChatPresenceUpdate friend, DateTimeOffset at, List<VRChatRecord> result)
    {
        if (_encounters.TryGetValue(friend.AccountId, out var existing) &&
            NameChanged(existing, "friend", friend.DisplayName))
        {
            result.Add(new("vrchat.encounter", existing with { EndedAt = at }));
            _encounters.Remove(friend.AccountId);
        }
    }

    private void EndVisit(DateTimeOffset at, bool eventBoundary, List<VRChatRecord> result)
    {
        if (eventBoundary)
        {
            if (_visit is not null) result.Add(new("vrchat.location", _visit with { EndedAt = at }));
            result.AddRange(_encounters.Values.Select(record => new VRChatRecord("vrchat.encounter", record with { EndedAt = at })));
        }
        _visit = null;
        _encounters.Clear();
    }

    private static bool NameChanged(RecordSnapshot record, string role, string? name) =>
        !string.IsNullOrWhiteSpace(name) && record.Objects.First(item => item.Role == role).Name != name;

    private void Break() { _visit = null; _location = null; _users.Clear(); _encounters.Clear(); }
    private static RecordSnapshot NewRecord(DateTimeOffset at, object value, params ObjectReference[] objects) =>
        new(Guid.CreateVersion7(at), at, at, null, JsonSerializer.SerializeToElement(value)) { Objects = objects };
}
