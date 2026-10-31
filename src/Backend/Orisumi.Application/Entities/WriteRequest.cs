using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Application.Entities;

public sealed record RecognitionKey(string? Scope, string Scheme, string Value);
public sealed record EntityReference(EntityId? EntityId, EntityId? SchemaId = null, RecognitionKey? Key = null);
public sealed record SchemaDeclaration(EntityId SchemaId, EntitySchema Content);
public sealed record FactTime(DateTimeOffset? StartAt, DateTimeOffset? EndAt, string? TimeZone);
public sealed record EntityWrite(EntityId SchemaId, JsonElement Content, string Formation,
    RecognitionKey? Key = null, FactTime? Time = null, VersionId[]? Inputs = null, string? StorageError = null);
public sealed record WriteRequest(EntityReference Observer, IReadOnlyList<EntityWrite> Writes,
    IReadOnlyList<SchemaDeclaration> Schemas, long? Sequence = null, ObserverDescription? Description = null);
public sealed record WriteStreamState(long LastSequence, Dictionary<EntityId, VersionId> SchemaVersionIds, VersionId? DescriptionVersionId = null);
public sealed record WriteError(int Status, string? Rule, string Detail);
public sealed record WriteResult(EntityVersion? Version, bool Changed, WriteError? Error = null);
public sealed record WriteResponse(IReadOnlyList<WriteResult> Results, bool Duplicate = false);

public sealed record ObserverDescription(EntityId SchemaId, JsonElement Content);
public sealed record Thing(EntityId EntityId, EntityId SchemaId, IReadOnlyList<RecognitionKey> Keys, string? Name = null);
