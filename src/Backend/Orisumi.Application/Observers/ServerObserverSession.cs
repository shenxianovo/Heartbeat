using System.Text.Json;
using Orisumi.Application.Entities;
using Orisumi.Core;

namespace Orisumi.Application.Observers;

public sealed class ServerObserverSession(EntityId instanceId, EntityService entities, IServerObserverStore store,
    Action<string>? reportDelivery = null)
{
    public EntityId InstanceId { get; } = instanceId;

    public async Task SubmitAsync(IEnumerable<VersionSubmission> versions, CancellationToken token)
    {
        // VERSION-7: persist the writer's original identities and content before saving any version.
        await store.SavePendingAsync(InstanceId, JsonSerializer.Serialize(versions.Select(VersionJson.Submission)), token);
        reportDelivery?.Invoke("pending");
        await ResumeAsync(token);
    }

    public async Task ResumeAsync(CancellationToken token)
    {
        if (await store.ReadPendingAsync(InstanceId, token) is not { } payload) return;
        reportDelivery?.Invoke("sending");
        using var document = JsonDocument.Parse(payload);
        var result = await entities.SubmitAsync(document.RootElement.EnumerateArray().Select(VersionJson.ReadSubmission), token);
        // A process exit or database failure before this acknowledgement leaves the exact batch for replay.
        await store.ClearPendingAsync(InstanceId, token);
        reportDelivery?.Invoke(result.Rejection is null ? "confirmed" : "rejected");
        result.RequireSuccess();
    }
}
