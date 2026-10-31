using System.Net;
using System.Text.Json;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.AssistantSessions;

internal sealed class AssistantDelivery(IObserverSession session, AssistantState state, Action<string> diagnostic)
{
    internal async Task<bool> CheckAsync(CancellationToken token)
    {
        var status = await session.RequestAsync("status", JsonLines.Json(new { }), cancellationToken: token);
        var rejected = status.At("rejection").At("version");
        if (rejected.ValueKind == JsonValueKind.Undefined) return true;
        var rule = rejected.Text("rule");
        var version = rejected.At("version");
        if (rule is not ("HISTORY-7" or "ENTITY-6"))
        { diagnostic("Delivery stopped: " + rule + ". " + rejected.Text("detail")); return false; }
        var entities = state.FindEntity(version.GetProperty("entityId").GetGuid());
        var deleted = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, entity) in entities)
        {
            if (rule == "HISTORY-7") deleted[key] = JsonLines.Json(entity with { Deleted = true });
            if (entity.SessionKey is { } sessionKey && state.Read<EntityEntry>(sessionKey) is { } parent)
                await CheckDeleted(sessionKey, parent);
            await CheckDeleted(key, entity);
        }
        if (deleted.Count == 0) { diagnostic("Delivery stopped: unresolved " + rule); return false; }
        // Mark first: a crash before discard retries the repair; a crash after it cannot resurrect the source identity.
        state.Apply(deleted);
        await session.RequestAsync("repair", JsonLines.Json(new { action = "discard", rejectedVersionId = version.GetProperty("versionId").GetGuid() }),
            "discard:" + version.GetProperty("versionId").GetString(), token);
        return true;

        async Task CheckDeleted(string key, EntityEntry entry)
        {
            if (deleted.ContainsKey(key)) return;
            try
            {
                var response = await session.RequestAsync("entityStatus", JsonLines.Json(new { entityId = entry.EntityId }), cancellationToken: token);
                if (response.GetProperty("status").GetInt32() == (int)HttpStatusCode.Gone)
                    deleted[key] = JsonLines.Json(entry with { Deleted = true });
            }
            catch (HttpRequestException) { }
        }
    }
}
