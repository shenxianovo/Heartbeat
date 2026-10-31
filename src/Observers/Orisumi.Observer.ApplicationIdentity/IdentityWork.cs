using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ApplicationIdentity;

// Turns formed versions into one batch; the commit returns once the backend saved all of them.
internal sealed class IdentityWork(IObserverSession session, EntityId instanceId)
{
    public EntityId InstanceId { get; } = instanceId;

    public async Task SubmitAsync(IEnumerable<VersionSubmission> versions, CancellationToken token)
    {
        var operations = new List<object>();
        var names = new Dictionary<VersionId, string>();
        foreach (var version in versions)
        {
            var name = "v" + names.Count;
            var bindings = (version.Content is BusinessContent ? Array.Empty<VersionReference>() : VersionRules.GetReferences(version, null))
                .Where(reference => reference.Position.StartsWith("/content/", StringComparison.Ordinal) && names.ContainsKey(reference.Target))
                .ToDictionary(reference => reference.Position[8..], reference => names[reference.Target], StringComparer.Ordinal);
            operations.Add(new { op = "write", entityId = version.EntityId.Value, category = version.Category, schemaVersionId = version.SchemaVersionId?.Value,
                previousVersionId = version.PreviousVersionId?.Value, content = VersionJson.Content(version.Content), bindings, @as = name });
            names[version.VersionId] = name;
        }
        await session.RequestAsync("commit", JsonLines.Json(new { operations }), token);
    }
}
