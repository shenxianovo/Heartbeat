using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

/// <summary>Owns canonical Fact custody and its rebuildable activity/input projections in one transaction.</summary>
public sealed partial class FactStore(AppDbContext db, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task IngestAsync(string ownerId, FactUploadRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (request.Streams is null || request.Facts is null || request.Gaps is null ||
            request.Streams.Count > 1000 || request.Facts.Count > 10000 || request.Gaps.Count > 10000)
            throw new FactIngestException("Invalid Fact batch.");
        var now = _time.GetUtcNow();
        var definitions = new Dictionary<Guid, FactStreamDefinition>();
        var schemas = new Dictionary<(Guid, int), ValidatedFactSchema>();
        foreach (var stream in request.Streams)
        {
            if (stream is null || stream.StreamId == Guid.Empty || stream.CollectorInstanceId == Guid.Empty ||
                stream.Subject is null || stream.Subject.SubjectId == Guid.Empty || stream.Subject.Kind is not ("machine" or "account" or "person") ||
                stream.Subject.Kind != "machine" && stream.Subject.HardwareId is not null ||
                string.IsNullOrWhiteSpace(stream.Source) || stream.Source.Length > 64 || string.IsNullOrWhiteSpace(stream.OutputId) ||
                string.IsNullOrWhiteSpace(stream.SchemaId) || stream.SchemaMajor <= 0 || stream.FactKind is not ("segment" or "event") ||
                stream.Schemas is null || stream.Dimensions is null || stream.Dimensions.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null) || !definitions.TryAdd(stream.StreamId, stream))
                throw new FactIngestException("Invalid or duplicate Fact Stream definition.");
            foreach (var schema in stream.Schemas)
                if (schema is null || !schemas.TryAdd((stream.StreamId, schema.Revision), FactIngestContract.Schema(stream, schema)))
                    throw new FactIngestException("Duplicate Fact Schema revision.");
        }
        foreach (var fact in request.Facts)
        {
            if (fact is null || !definitions.TryGetValue(fact.StreamId, out var definition) || !schemas.TryGetValue((fact.StreamId, fact.SchemaRevision), out var schema))
                throw new FactIngestException("Every Fact must include its Stream and exact Schema document.");
            FactIngestContract.Snapshot(fact, definition.FactKind, schema, now);
        }
        foreach (var gap in request.Gaps)
            if (gap is null || gap.GapId == Guid.Empty || gap.GapId.Version != 7 || !definitions.ContainsKey(gap.StreamId) || gap.Start.Offset != TimeSpan.Zero ||
                gap.End.Offset != TimeSpan.Zero || gap.Start > gap.End || gap.End > now.AddMinutes(5) || string.IsNullOrWhiteSpace(gap.Reason) || gap.EstimatedFactsLost < 0)
                throw new FactIngestException("Invalid Fact Stream gap.");

        await Atomic(ownerId, async () =>
        {
            var streams = new Dictionary<Guid, FactStream>();
            foreach (var definition in definitions.Values)
                streams.Add(definition.StreamId, await RegisterStream(ownerId, definition, ct));
            foreach (var fact in request.Facts)
                await Apply(streams[fact.StreamId], fact, schemas[(fact.StreamId, fact.SchemaRevision)], ct);
            foreach (var gap in request.Gaps)
            {
                var existing = await db.FactGaps.FindAsync([ownerId, gap.StreamId, gap.GapId], ct);
                if (existing is not null)
                {
                    if (existing.Start != gap.Start || existing.End != gap.End || existing.Reason != gap.Reason || existing.EstimatedFactsLost != gap.EstimatedFactsLost)
                        throw new FactIngestException("The same Gap identity has different content.", true);
                    continue;
                }
                db.FactGaps.Add(new FactGap { OwnerId = ownerId, StreamId = gap.StreamId, GapId = gap.GapId, Start = gap.Start, End = gap.End, Reason = gap.Reason, EstimatedFactsLost = gap.EstimatedFactsLost });
            }
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    private async Task Atomic(string ownerId, Func<Task> action, CancellationToken ct = default)
    {
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            // One owner lock makes legacy takeover, batches, and App resolution serializable together.
            // Schema identities are Owner-scoped as uploaded documents are not deployment-global authority.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"fact-owner:" + ownerId}, 0))", ct);
            await action();
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<FactStream> RegisterStream(string ownerId, FactStreamDefinition definition, CancellationToken ct)
    {
        var subject = await db.FactSubjects.FindAsync([ownerId, definition.Subject.SubjectId], ct);
        if (subject is not null && subject.Kind != definition.Subject.Kind)
            throw new FactIngestException("Subject identity cannot change kind.", true);
        if (subject is null)
        {
            Device? device = null;
            if (definition.Subject.Kind == "machine")
            {
                if (string.IsNullOrWhiteSpace(definition.Subject.HardwareId))
                    throw new FactIngestException("A new Machine Subject requires its hardware identity.");
                if (Guid.TryParse(definition.Subject.HardwareId, out var hardwareGuid))
                {
                    var devices = await db.Devices.Where(d => d.OwnerId == ownerId).ToListAsync(ct);
                    device = devices.SingleOrDefault(d => Guid.TryParse(d.HardwareId, out var existingGuid) && existingGuid == hardwareGuid);
                }
                device ??= await new DeviceService(db).ResolveByHardwareIdAsync(ownerId, definition.Subject.HardwareId, definition.Subject.DisplayName);
            }
            subject = new FactSubjectRecord { OwnerId = ownerId, SubjectId = definition.Subject.SubjectId, Kind = definition.Subject.Kind, DeviceId = device?.Id, DisplayName = definition.Subject.DisplayName };
            db.FactSubjects.Add(subject);
        }
        else if (definition.Subject.HardwareId is { } hardwareId)
        {
            var device = await db.Devices.FindAsync([subject.DeviceId], ct);
            if (device?.HardwareId != hardwareId && !(Guid.TryParse(device?.HardwareId, out var oldGuid) && Guid.TryParse(hardwareId, out var newGuid) && oldGuid == newGuid)) throw new FactIngestException("Subject hardware identity cannot change.", true);
        }
        var dimensions = FactIngestContract.Canonical(JsonSerializer.SerializeToElement(definition.Dimensions));
        var stream = await db.FactStreams.FindAsync([ownerId, definition.StreamId], ct);
        if (stream is not null)
        {
            if (stream.SubjectId != subject.SubjectId || stream.CollectorInstanceId != definition.CollectorInstanceId || stream.OutputId != definition.OutputId ||
                stream.Source != definition.Source || stream.FactKind != definition.FactKind || stream.SchemaId != definition.SchemaId || stream.SchemaMajor != definition.SchemaMajor ||
                !JsonElement.DeepEquals(JsonDocument.Parse(stream.Dimensions).RootElement, JsonDocument.Parse(dimensions).RootElement))
                throw new FactIngestException("Fact Stream identity changed.", true);
            stream.Subject = subject;
        }
        else
        {
            stream = new FactStream { OwnerId = ownerId, StreamId = definition.StreamId, SubjectId = subject.SubjectId, CollectorInstanceId = definition.CollectorInstanceId,
                OutputId = definition.OutputId, Source = definition.Source, FactKind = definition.FactKind, SchemaId = definition.SchemaId, SchemaMajor = definition.SchemaMajor,
                Dimensions = dimensions, Subject = subject };
            db.FactStreams.Add(stream);
        }
        foreach (var schema in definition.Schemas.OrderBy(s => s.Revision))
        {
            var existing = await db.FactSchemas.FindAsync([ownerId, definition.SchemaId, definition.SchemaMajor, schema.Revision], ct);
            if (existing is not null && existing.ContentHash != schema.ContentHash &&
                !JsonElement.DeepEquals(JsonDocument.Parse(existing.DocumentJson).RootElement,
                    JsonDocument.Parse(schema.DocumentJson).RootElement))
                throw new FactIngestException("Published Fact Schema revision changed content.", true);
            if (existing is null)
                db.FactSchemas.Add(new FactSchemaRecord { OwnerId = ownerId, SchemaId = definition.SchemaId, SchemaMajor = definition.SchemaMajor, Revision = schema.Revision, ContentHash = schema.ContentHash, DocumentJson = schema.DocumentJson });
        }
        await db.SaveChangesAsync(ct);
        return stream;
    }

    private async Task Apply(FactStream stream, FactSnapshot snapshot, ValidatedFactSchema schema, CancellationToken ct)
    {
        var fact = await db.Facts.SingleOrDefaultAsync(f => f.OwnerId == stream.OwnerId && f.StreamId == stream.StreamId && f.FactId == snapshot.FactId, ct);
        var hash = FactIngestContract.SnapshotHash(snapshot);
        if (fact is not null)
        {
            if (snapshot.Revision < fact.Revision) return;
            if (snapshot.Revision == fact.Revision)
            {
                if (hash != fact.ContentHash) throw new FactIngestException("The same Fact Revision has different content.", true);
                return;
            }
            if (stream.FactKind == "segment" && (fact.Start != snapshot.Start || fact.IsFinal == true && snapshot.IsFinal != true) ||
                stream.FactKind == "event" && fact.OccurredAt != snapshot.OccurredAt)
                throw new FactIngestException("Fact Revision violates its evolution rules.", true);
            if (stream.FactKind == "event" &&
                (schema.EvolutionMode != "mutableEvent" || !FactIngestContract.HasOnlyMutableChanges(fact.Payload!, snapshot.Payload!.Value.GetRawText(), schema)))
                throw new FactIngestException("Event Revision changes immutable content.", true);
        }
        else
        {
            fact = await FindLegacy(stream, snapshot, ct) ?? new ObservedFact { Id = Guid.CreateVersion7(), OwnerId = stream.OwnerId };
            if (db.Entry(fact).State == EntityState.Detached) db.Facts.Add(fact);
        }
        fact.StreamId = stream.StreamId;
        fact.FactId = snapshot.FactId;
        fact.Stream = stream;
        fact.Origin = "native";
        // Lineage exists even if native delivery wins the upgrade race.
        fact.LegacyId ??= stream.FactKind == "segment" ? FactIngestContract.ProjectedSegmentId(stream.StreamId, snapshot.FactId) : snapshot.FactId;
        fact.LegacyKind ??= stream.FactKind;
        fact.LegacyDeviceId ??= stream.Subject.DeviceId;
        fact.Revision = snapshot.Revision;
        fact.SchemaRevision = snapshot.SchemaRevision;
        fact.ObservedAt = snapshot.ObservedAt;
        fact.Start = snapshot.Start;
        fact.End = snapshot.End;
        fact.IsFinal = snapshot.IsFinal;
        fact.OccurredAt = snapshot.OccurredAt;
        fact.Payload = snapshot.Payload?.GetRawText();
        fact.ContentHash = hash;
        await Project(fact, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task<ObservedFact?> FindLegacy(FactStream stream, FactSnapshot snapshot, CancellationToken ct)
    {
        var legacyId = stream.FactKind == "segment" ? FactIngestContract.ProjectedSegmentId(stream.StreamId, snapshot.FactId) : snapshot.FactId;
        var candidates = await db.Facts.Include(f => f.Stream).ThenInclude(s => s.Subject)
            .Where(f => f.OwnerId == stream.OwnerId && f.Origin == "legacy-import" && f.LegacyId == legacyId && f.LegacyKind == stream.FactKind).ToListAsync(ct);
        var fact = candidates.SingleOrDefault(f =>
            stream.Subject.DeviceId is { } deviceId ? f.LegacyDeviceId == deviceId : f.Stream.SubjectId == stream.SubjectId);
        if (fact is null) return null;
        if (fact.Stream.Source != stream.Source || fact.Stream.Subject.Kind != stream.Subject.Kind)
            throw new FactIngestException("Legacy Fact belongs to a different Subject or Source.", true);
        if (stream.FactKind == "event" && (LegacyTimestamp(fact.OccurredAt) != LegacyTimestamp(snapshot.OccurredAt) || snapshot.Payload is not { } payload ||
            !JsonElement.DeepEquals(JsonDocument.Parse(fact.Payload!).RootElement, payload)))
            throw new FactIngestException("Legacy InputEvent identity has incompatible content.", true);
        return fact;
    }

    // Only legacy takeover uses the old Npgsql microsecond encoding (epoch 2000-01-01).
    // This also preserves its truncation direction for pre-2000 timestamps. Native comparisons stay exact.
    private static long? LegacyTimestamp(DateTimeOffset? value) => value is { } time
        ? (time.UtcTicks - 630822816000000000L) / TimeSpan.TicksPerMicrosecond : null;

    private async Task Project(ObservedFact fact, CancellationToken ct)
    {
        var stream = fact.Stream;
        var segment = await db.ActivitySegments.SingleOrDefaultAsync(s => s.FactKey == fact.Id, ct);
        var input = await db.InputEvents.SingleOrDefaultAsync(e => e.FactKey == fact.Id, ct);
        var payload = JsonDocument.Parse(fact.Payload!).RootElement;
        if (stream.FactKind == "segment")
        {
            var identityKey = String(payload, "identityKey");
            // A valid Segment without the activity vocabulary is still a canonical Fact.
            if (identityKey is null)
            {
                if (segment is not null) db.ActivitySegments.Remove(segment);
                return;
            }
            var dimensions = JsonDocument.Parse(stream.Dimensions).RootElement;
            var appKey = String(dimensions, "appIdentityKey") ?? String(payload, "appIdentityKey");
            AppIdentity? app;
            try { app = appKey is null ? null : await new AppIdentityService(db).ResolveAsync(appKey, String(payload, "appDisplayName")); }
            catch (ArgumentException ex) { throw new FactIngestException(ex.Message); }
            if (segment is null)
            {
                segment = new ActivitySegment { Id = fact.Id, FactKey = fact.Id };
                db.ActivitySegments.Add(segment);
            }
            segment.Fact = fact;
            segment.OwnerId = stream.OwnerId;
            segment.DeviceId = stream.Subject.DeviceId;
            segment.Source = stream.Source;
            segment.IdentityKey = identityKey;
            segment.Title = String(payload, "title");
            segment.AppIdentityId = app?.Id;
            segment.AppId = app?.AppId;
            segment.StartTime = fact.Start!.Value;
            segment.EndTime = fact.End!.Value;
            segment.Payload = fact.Payload;
            segment.Attributes = payload.TryGetProperty("attributes", out var attributes) ? attributes.GetRawText() : null;
        }
        else if (stream.SchemaId == "heartbeat.input" && stream.SchemaMajor == 1 && stream.Subject.DeviceId is { } deviceId)
        {
            var item = new InputEventItem { Id = fact.FactId, Timestamp = fact.OccurredAt!.Value, CodeSet = String(payload, "codeSet") ?? "", Code = payload.GetProperty("code").GetInt16(),
                EventType = String(payload, "eventType") switch { "keyDown" => InputEventType.KeyDown, "mouseButton" => InputEventType.MouseButton, "mouseScroll" => InputEventType.MouseScroll, _ => throw new FactIngestException("Unknown input event type.") } };
            InputEventIngestContract.Validate([item]);
            if (input is null)
            {
                input = new InputEvent { Id = fact.Id, FactKey = fact.Id };
                db.InputEvents.Add(input);
            }
            input.Fact = fact;
            input.DeviceId = deviceId;
            input.EventType = item.EventType;
            input.CodeSet = item.CodeSet;
            input.Code = item.Code;
            input.Timestamp = item.Timestamp;
        }
    }

    internal static string? String(JsonElement payload, string property) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static string EventName(InputEventType type) => type switch { InputEventType.KeyDown => "keyDown", InputEventType.MouseButton => "mouseButton", InputEventType.MouseScroll => "mouseScroll", _ => throw new FactIngestException("Unknown input event type.") };
}
