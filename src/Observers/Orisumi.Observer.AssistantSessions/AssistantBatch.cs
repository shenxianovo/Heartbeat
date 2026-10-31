using System.Globalization;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.AssistantSessions;

internal sealed class AssistantBatch(Ref? device)
{
    internal List<WriteItem> Writes { get; } = [];
    internal Dictionary<string, JsonElement> Changes { get; } = new(StringComparer.Ordinal);
    internal int OperationCount => Writes.Count;
    internal void Set<T>(string key, T value) => Changes[key] = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
    private static RecognitionKey Key(string product, string type, string value) => new(null, product + "." + type, value);
    private static Ref SessionRef(SessionInfo info) => Ref.Recognize(AssistantRegistration.SessionSchemaId, Key(info.Product, "session", info.SourceSessionId));

    internal void Session(SessionInfo info, string? titleTimestamp = null)
    {
        Writes.Add(new(AssistantRegistration.SessionSchemaId, new SessionContent(info.Product, info.SourceSessionId), Key(info.Product, "session", info.SourceSessionId)));
        if (info.ParentId is { } parentId)
        {
            if (parentId == info.SourceSessionId) throw new InvalidDataException("A source session cannot be its own parent.");
            var parent = new SessionInfo(info.Product, parentId);
            Session(parent);
            Writes.Add(new(AssistantRegistration.SubagentSchemaId,
                new SubagentContent(SessionRef(info), SessionRef(parent), info.AgentName, info.SpawnCallId), Key(info.Product, "subagent", info.SourceSessionId)));
        }
        if (info.Title is { } title && (info.Product == "claude-code" || titleTimestamp is not null))
        {
            var time = titleTimestamp is null ? FactTime.Unknown() : FactTime.Point(VersionTime.Normalize(DateTimeOffset.Parse(titleTimestamp, CultureInfo.InvariantCulture)), null);
            Writes.Add(new(AssistantRegistration.TitleSchemaId, new TitleContent(SessionRef(info), title),
                Key(info.Product, "session-title", info.SourceSessionId + "\n" + (titleTimestamp ?? title)), Time: time));
        }
    }

    internal void Add(SessionInfo info, Interaction interaction)
    {
        var call = interaction.Kind == "tool-call";
        var value = call ? interaction.CallId! : interaction.SourceId + (info.Product == "codex" && interaction.Kind == "tool-result" ? "/result" : "");
        var key = Key(info.Product, call ? "tool-call" : "interaction", value);
        var tool = interaction.Kind == "tool-result" && interaction.CallId is { } id
            ? Ref.Recognize(AssistantRegistration.InteractionSchemaId, Key(info.Product, "tool-call", id)) : null;
        var content = new InteractionContent(SessionRef(info), device,
            interaction.Kind, interaction.Actor, interaction.SourceRole, interaction.SourceKind, interaction.SourceId, info.Client,
            interaction.SourceVersion, interaction.SourceTurnId, interaction.SourceParentId, interaction.SourceMessageId, interaction.CallId,
            tool, interaction.ToolName, interaction.ToolServer, interaction.IsError, interaction.ExitCode, interaction.Phase,
            interaction.Model, interaction.WorkingDirectory, interaction.Parts,
            interaction.SourceEntries.Select(bytes => new BinaryContent(bytes, "application/json")).ToArray());
        Writes.Add(new(AssistantRegistration.InteractionSchemaId, content, key, Time: new(
            interaction.StartAt is { } start ? VersionTime.Normalize(start) : null,
            interaction.EndAt is { } end ? VersionTime.Normalize(end) : null, interaction.TimeZone)));
    }
}
internal sealed record SessionContent(string Product, string SourceSessionId);
internal sealed record TitleContent(Ref SessionVersionId, string Title);
internal sealed record SubagentContent(Ref SessionVersionId, Ref ParentSessionVersionId, string? AgentName, string? SpawnCallId);
internal sealed record InteractionContent(Ref SessionVersionId, Ref? DeviceVersionId, string Kind, string? Actor,
    string? SourceRole, string SourceKind, string SourceId, string? Client, string? SourceVersion, string? SourceTurnId,
    string? SourceParentId, string? SourceMessageId, string? CallId, Ref? ToolCallVersionId, string? ToolName, string? ToolServer,
    bool? IsError, int? ExitCode, string? Phase, string? Model, string? WorkingDirectory, IReadOnlyList<ContentPart> Parts,
    IReadOnlyList<BinaryContent> SourceEntries);
