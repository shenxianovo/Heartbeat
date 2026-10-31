using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.AssistantSessions;

internal sealed class AssistantBatch(AssistantState state, EntityId observerId, AssistantRegistration registration)
{
    private readonly List<object> _operations = [];
    private readonly Dictionary<Guid, string> _references = [];
    private readonly Dictionary<string, BinaryContent> _contents = new(StringComparer.Ordinal);
    internal Dictionary<string, JsonElement> Changes { get; } = new(StringComparer.Ordinal);
    internal int OperationCount => _operations.Count;
    internal JsonElement Payload => JsonLines.Json(new { operations = _operations, contents = _contents.Values.Select(content => content.Payload) });

    internal T? Read<T>(string key) => Changes.TryGetValue(key, out var value)
        ? value.Deserialize<T>(JsonSerializerOptions.Web) : state.Read<T>(key);
    internal void Set<T>(string key, T value) => Changes[key] = JsonLines.Json(value!);

    internal EntityEntry Session(SessionInfo info)
    {
        var key = AssistantState.Key("session", info.Product, info.SourceSessionId);
        var existing = Read<EntityEntry>(key);
        if (existing?.Deleted == true) return existing;
        var previous = existing?.Session;
        var merged = info with { Title = info.Title ?? previous?.Title, Client = info.Client ?? previous?.Client,
            ParentId = info.ParentId ?? previous?.ParentId, AgentName = info.AgentName ?? previous?.AgentName,
            SpawnCallId = info.SpawnCallId ?? previous?.SpawnCallId };
        var entry = existing ?? new(EntityId.New().Value);
        if (merged == previous) return entry;
        EntityEntry? parent = null;
        if (merged.ParentId is { } parentId)
        {
            if (parentId == merged.SourceSessionId) throw new InvalidDataException("A source session cannot be its own parent.");
            parent = Session(new(merged.Product, parentId));
            if (parent.Deleted) { entry = entry with { Session = merged, Deleted = true }; Set(key, entry); return entry; }
        }
        var bindings = new Dictionary<string, string>();
        if (parent is not null) bindings["/parentSessionVersionId"] = Reference(parent.EntityId);
        Draft(entry.EntityId, registration.SessionSchema, new { merged.Product, merged.SourceSessionId, merged.Title, merged.Client,
            parentSessionVersionId = parent is null ? (Guid?)null : Guid.Empty, merged.AgentName, merged.SpawnCallId }, bindings);
        _operations.Add(new { op = "complete", entityId = entry.EntityId });
        _references.Remove(entry.EntityId); // A metadata change needs a reference to the newly formed version.
        entry = entry with { Session = merged };
        Set(key, entry);
        return entry;
    }

    internal void Add(SessionInfo info, Interaction interaction)
    {
        var key = AssistantState.Key("interaction", info.Product, interaction.Kind, interaction.SourceId);
        if (Read<EntityEntry>(key) is not null) return;
        var session = Session(info);
        if (session.Deleted) return;
        var entry = new EntityEntry(EntityId.New().Value, EntityId.New().Value,
            AssistantState.Key("session", info.Product, info.SourceSessionId));
        var bindings = new Dictionary<string, string> { ["/sessionVersionId"] = Reference(session.EntityId) };
        var call = interaction.CallId is { } callId
            ? Read<EntityEntry>(AssistantState.Key("call", info.Product, info.SourceSessionId, callId)) : null;
        var hasCall = interaction.Kind == "tool-result" && call is { Deleted: false };
        if (hasCall) bindings["/toolCallVersionId"] = Reference(call!.EntityId);
        foreach (var bytes in interaction.SourceEntries)
        {
            var binary = new BinaryContent(bytes, "application/json");
            _contents[binary.Digest] = binary;
        }
        foreach (var part in interaction.Parts)
            if (part.File is { } binary) _contents[binary.Digest] = binary;
        Draft(entry.EntityId, registration.InteractionSchema, new
        {
            sessionVersionId = Guid.Empty, interaction.Kind, interaction.Actor, interaction.SourceRole, interaction.SourceKind,
            interaction.SourceId, interaction.SourceVersion, interaction.SourceTurnId, interaction.SourceParentId, interaction.SourceMessageId,
            interaction.CallId, toolCallVersionId = hasCall ? (Guid?)Guid.Empty : null, interaction.ToolName, interaction.ToolServer,
            interaction.IsError, interaction.ExitCode, interaction.Phase, interaction.Model, interaction.WorkingDirectory,
            parts = interaction.Parts.Select(part => new { part.Type, part.Text, part.EntryIndex, part.Pointer, part.MediaType, file = part.File?.Descriptor }),
            sourceEntries = interaction.SourceEntries.Select(bytes => new BinaryContent(bytes, "application/json").Descriptor),
        }, bindings);
        _operations.Add(new { op = "complete", entityId = entry.EntityId });
        var contentReference = Reference(entry.EntityId);
        var observerReference = Reference(observerId.Value);
        _operations.Add(new { op = "draft", entityId = entry.ObservationId, category = "observation", strategy = "OnCompletion",
            content = new { observerVersionId = Guid.Empty, contentVersionId = Guid.Empty, inputSetVersionId = (Guid?)null,
                startAt = interaction.StartAt is { } start ? VersionTime.Normalize(start) : (DateTimeOffset?)null,
                endAt = interaction.EndAt is { } end ? VersionTime.Normalize(end) : (DateTimeOffset?)null, interaction.TimeZone },
            bindings = new Dictionary<string, string> { ["/observerVersionId"] = observerReference, ["/contentVersionId"] = contentReference } });
        _operations.Add(new { op = "complete", entityId = entry.ObservationId });
        Set(key, entry);
        if (interaction.Kind == "tool-call" && interaction.CallId is { } id)
            Set(AssistantState.Key("call", info.Product, info.SourceSessionId, id), entry);
    }

    private string Reference(Guid id)
    {
        if (_references.TryGetValue(id, out var name)) return name;
        name = "r" + _operations.Count;
        _operations.Add(new { op = "reference", entityId = id, @as = name });
        _references[id] = name;
        return name;
    }

    private void Draft(Guid id, VersionId schema, object content, Dictionary<string, string> bindings) =>
        _operations.Add(new { op = "draft", entityId = id, category = "business", strategy = "OnCompletion",
            schemaVersionId = schema.Value, content, bindings });
}
