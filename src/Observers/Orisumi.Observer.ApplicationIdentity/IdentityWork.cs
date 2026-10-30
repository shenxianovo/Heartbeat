using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ApplicationIdentity;

// Only business recovery state lives here; versions and delivery receipts belong to the Runtime.
internal sealed class IdentityWork(IObserverSession session, EntityId instanceId, string directory)
{
    public EntityId InstanceId { get; } = instanceId;
    private string PendingPath => Path.Combine(directory, "pending.json");
    private string StatePath => Path.Combine(directory, "unknown.json");
    public string? ReadState() => File.Exists(StatePath) ? File.ReadAllText(StatePath) : null;
    public void SaveState(string payload) => Save(StatePath, payload);
    private static void Save(string path, string payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write(payload); writer.Flush(); stream.Flush(true);
        }
        File.Move(path + ".tmp", path, true);
    }
    public async Task SubmitAsync(IEnumerable<VersionSubmission> versions, CancellationToken token)
    {
        if (File.Exists(PendingPath)) throw new InvalidOperationException("Resume the pending batch first.");
        var operations = new List<object>();
        var names = new Dictionary<VersionId, string>();
        foreach (var version in versions)
        {
            var name = "v" + names.Count;
            var bindings = (version.Content is BusinessContent ? Array.Empty<VersionReference>() : VersionRules.GetReferences(version, null))
                .Where(reference => reference.Position.StartsWith("/content/", StringComparison.Ordinal) && names.ContainsKey(reference.Target))
                .ToDictionary(reference => reference.Position[8..], reference => names[reference.Target], StringComparer.Ordinal);
            operations.Add(new { op = "draft", entityId = version.EntityId.Value, category = version.Category, schemaVersionId = version.SchemaVersionId?.Value,
                previousVersionId = version.PreviousVersionId?.Value, content = VersionJson.Content(version.Content), strategy = "OnCompletion", bindings });
            operations.Add(new { op = "complete", entityId = version.EntityId.Value });
            operations.Add(new { op = "reference", entityId = version.EntityId.Value, @as = name });
            names[version.VersionId] = name;
        }
        Save(PendingPath, JsonSerializer.Serialize(new { id = Guid.CreateVersion7().ToString(), method = "commit", @params = new { operations } }));
        await ResumeAsync(token);
    }
    public async Task<string?> RestoreHandoffAsync(CancellationToken token)
    {
        if (!File.Exists(PendingPath)) return null;
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(PendingPath, token));
        var batch = json.RootElement;
        var id = batch.GetProperty("id").GetString()!;
        await session.RequestAsync("commit", batch.GetProperty("params"), id, token);
        return id;
    }
    public async Task ResumeAsync(CancellationToken token)
    {
        var id = await RestoreHandoffAsync(token);
        if (id is null) return;
        while (true)
        {
            var outcome = await session.RequestAsync("outcome", JsonLines.Json(new { batchId = id, wait = 30 }), cancellationToken: token);
            switch (outcome.GetProperty("state").GetString())
            {
                case "pending": continue;
                case "saved": File.Delete(PendingPath); return;
                case "rejected":
                    // A known rejection ends replay. Discard its unsaved dependents before a fresh business decision.
                    await session.RequestAsync("repair", JsonLines.Json(new { rejectedVersionId = outcome.GetProperty("versionId").GetGuid(), action = "discard" }),
                        "discard:" + id, token);
                    File.Delete(PendingPath);
                    throw new ModelViolationException(outcome.GetProperty("rule").GetString()!, outcome.GetProperty("detail").GetString()!);
                case "discarded": File.Delete(PendingPath); return;
                default: throw new InvalidDataException("Unknown batch outcome.");
            }
        }
    }
}
