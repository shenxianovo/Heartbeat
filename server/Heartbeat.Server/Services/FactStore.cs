using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.Facts;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

/// <summary>Atomically owns the latest Segment/Event snapshots and their Subject/Stream identities.</summary>
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
        foreach (var stream in request.Streams)
        {
            if (stream is null || stream.StreamId == Guid.Empty || stream.CollectorInstanceId == Guid.Empty ||
                stream.Subject is null || stream.Subject.SubjectId == Guid.Empty || stream.Subject.Kind is not ("machine" or "account" or "person") ||
                stream.Subject.Kind != "machine" && stream.Subject.HardwareId is not null ||
                string.IsNullOrWhiteSpace(stream.Source) || stream.Source.Length > 64 || string.IsNullOrWhiteSpace(stream.OutputId) ||
                stream.FactKind is not ("segment" or "event") ||
                stream.Dimensions is null || stream.Dimensions.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null) || !definitions.TryAdd(stream.StreamId, stream))
                throw new FactIngestException("Invalid or duplicate Fact Stream definition.");
        }
        foreach (var fact in request.Facts)
        {
            if (fact is null || !definitions.TryGetValue(fact.StreamId, out var definition))
                throw new FactIngestException("Every Fact must include its Stream definition.");
            FactIngestContract.Snapshot(fact, definition.FactKind, now);
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
                await Apply(streams[fact.StreamId], fact, ct);
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
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"fact-owner:" + ownerId}, 0))", ct);
            await AppCatalogLock.AcquireAsync(db, ct);
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
                device = await new DeviceService(db).ResolveFactReferenceAsync(ownerId,
                    definition.Subject.HardwareId, definition.Subject.DisplayName, ct);
            }
            subject = new FactSubjectRecord { OwnerId = ownerId, SubjectId = definition.Subject.SubjectId, Kind = definition.Subject.Kind, DeviceId = device?.Id, DisplayName = definition.Subject.DisplayName };
            db.FactSubjects.Add(subject);
        }
        else if (definition.Subject.HardwareId is { } hardwareId)
        {
            var device = await db.Devices.FindAsync([subject.DeviceId], ct);
            if (!DeviceService.SameHardwareIdentity(device?.HardwareId, hardwareId)) throw new FactIngestException("Subject hardware identity cannot change.", true);
        }
        var dimensions = FactIngestContract.Canonical(JsonSerializer.SerializeToElement(definition.Dimensions));
        var stream = await db.FactStreams.FindAsync([ownerId, definition.StreamId], ct);
        if (stream is not null)
        {
            if (stream.SubjectId != subject.SubjectId || stream.CollectorInstanceId != definition.CollectorInstanceId || stream.OutputId != definition.OutputId ||
                stream.Source != definition.Source || stream.FactKind != definition.FactKind ||
                !JsonElement.DeepEquals(JsonDocument.Parse(stream.Dimensions).RootElement, JsonDocument.Parse(dimensions).RootElement))
                throw new FactIngestException("Fact Stream identity changed.", true);
            stream.Subject = subject;
        }
        else
        {
            stream = new FactStream
            {
                OwnerId = ownerId,
                StreamId = definition.StreamId,
                SubjectId = subject.SubjectId,
                CollectorInstanceId = definition.CollectorInstanceId,
                OutputId = definition.OutputId,
                Source = definition.Source,
                FactKind = definition.FactKind,
                Dimensions = dimensions,
                Subject = subject
            };
            db.FactStreams.Add(stream);
        }
        await db.SaveChangesAsync(ct);
        return stream;
    }

    private async Task Apply(FactStream stream, FactSnapshot snapshot, CancellationToken ct)
    {
        var payload = snapshot.Aspect is null ? StoredPayload(snapshot.Payload!.Value, stream.FactKind) : JsonDocument.Parse(snapshot.Payload!.Value.GetRawText());
        var aspect = snapshot.Aspect ?? FactAspectCompatibility.Infer(stream.Source, stream.FactKind, payload.RootElement);
        var start = NormalizeTime(snapshot.Start);
        var end = NormalizeTime(snapshot.End);
        var at = NormalizeTime(snapshot.OccurredAt);
        IFactRecord? fact = stream.FactKind == "segment"
            ? await db.Segments.SingleOrDefaultAsync(f => f.OwnerId == stream.OwnerId && f.StreamId == stream.StreamId && f.FactId == snapshot.FactId, ct)
            : await db.Events.SingleOrDefaultAsync(f => f.OwnerId == stream.OwnerId && f.StreamId == stream.StreamId && f.FactId == snapshot.FactId, ct);
        if (fact is not null && snapshot.Revision < fact.Revision) return;
        var observation = await ResolveObservation(stream, snapshot, payload.RootElement, aspect, ct,
            snapshot.ObserverId is null && snapshot.Target is null ? fact?.AppIdentityId : null);
        if (fact is not null)
        {
            var sameTimes = fact is Segment segment
                ? segment.StartTime == start && segment.EndTime == end
                : ((Event)fact).Timestamp == at;
            if (snapshot.Revision == fact.Revision)
            {
                if (!sameTimes || fact.Aspect != aspect || fact.ObserverId != observation.CollectorId || fact.FoiId != observation.FoiId ||
                    !await SameRelations(fact.Id, observation.Relations, ct) ||
                    !JsonElement.DeepEquals(fact.Payload.RootElement, payload.RootElement))
                    throw new FactIngestException("The same Fact Revision has different content.", true);
                return;
            }
            if (fact is Segment oldSegment ? oldSegment.StartTime != start : ((Event)fact).Timestamp != at)
                throw new FactIngestException("Fact Revision cannot change its start/occurrence.", true);
        }
        else
        {
            fact = await FindLegacy(stream, snapshot, payload, ct);
            if (fact is null)
            {
                if (stream.FactKind == "segment")
                {
                    var segment = new Segment { Id = Guid.CreateVersion7(), OwnerId = stream.OwnerId };
                    db.Segments.Add(segment);
                    fact = segment;
                }
                else
                {
                    var item = new Event { Id = Guid.CreateVersion7(), OwnerId = stream.OwnerId };
                    db.Events.Add(item);
                    fact = item;
                }
            }
        }
        fact.StreamId = stream.StreamId;
        fact.Stream = stream;
        fact.FactId = snapshot.FactId;
        fact.Source = stream.Source;
        fact.Aspect = aspect;
        fact.ObserverId = observation.CollectorId;
        fact.FoiId = observation.FoiId;
        fact.TargetKind = null;
        fact.TargetId = null;
        fact.Revision = snapshot.Revision;
        fact.Payload = payload;
        fact.AppIdentityId = observation.AppIdentityId;
        if (fact is Segment savedSegment)
        {
            savedSegment.StartTime = start!.Value;
            savedSegment.EndTime = end!.Value;
        }
        else ((Event)fact).Timestamp = at!.Value;
        await db.SaveChangesAsync(ct);
        await WriteRelations(fact, observation.Relations, ct);
    }

    private async Task<long?> ResolveApp(FactStream stream, JsonElement payload, string? aspect, CancellationToken ct)
    {
        if (aspect is not (FactAspects.DesktopActivity or FactAspects.SelectedPage or FactAspects.Activity)) return null;
        var dimensions = JsonDocument.Parse(stream.Dimensions).RootElement;
        var key = String(dimensions, "appIdentityKey") ?? String(payload, "appIdentityKey");
        if (key is null) return null;
        try { return (await new AppIdentityService(db).ResolveAsync(key, String(payload, "appDisplayName"))).Id; }
        catch (ArgumentException) { return null; }
    }

    private async Task<IFactRecord?> FindLegacy(FactStream stream, FactSnapshot snapshot, JsonDocument payload, CancellationToken ct)
    {
        var legacyId = stream.FactKind == "segment" ? FactIngestContract.ProjectedSegmentId(stream.StreamId, snapshot.FactId) : snapshot.FactId;
        IFactRecord? fact = stream.FactKind == "segment"
            ? await db.Segments.Include(f => f.Stream).ThenInclude(s => s.Subject)
                .SingleOrDefaultAsync(f => f.Id == legacyId && f.OwnerId == stream.OwnerId && f.Stream.Origin == "legacy-import", ct)
            : await db.Events.Include(f => f.Stream).ThenInclude(s => s.Subject)
                .SingleOrDefaultAsync(f => f.Id == legacyId && f.OwnerId == stream.OwnerId && f.Stream.Origin == "legacy-import", ct);
        if (fact is null) return null;
        if (!SameSubject(fact.Stream.Subject, stream.Subject) || fact.Source != stream.Source)
            throw new FactIngestException("Legacy Fact belongs to a different Subject or Source.", true);
        if (fact is Event input && (input.Timestamp != NormalizeTime(snapshot.OccurredAt) ||
            !JsonElement.DeepEquals(input.Payload.RootElement, payload.RootElement)))
            throw new FactIngestException("Legacy InputEvent identity has incompatible content.", true);
        return fact;
    }

    private static bool SameSubject(FactSubjectRecord first, FactSubjectRecord second) =>
        first.OwnerId == second.OwnerId && first.Kind == second.Kind &&
        (first.Kind == "machine" ? first.DeviceId == second.DeviceId : first.SubjectId == second.SubjectId);

    // Match Npgsql's integer truncation relative to its 2000-01-01 epoch, including earlier dates.
    internal static DateTimeOffset? NormalizeTime(DateTimeOffset? value) => value is { } time
        ? new DateTimeOffset(630822816000000000L + (time.UtcTicks - 630822816000000000L) / 10 * 10, TimeSpan.Zero) : null;

    internal static JsonDocument StoredPayload(JsonElement payload, string kind)
    {
        try { return JsonDocument.Parse((kind == "segment" ? Heartbeat.Core.Facts.ActivityFactPayload.Normalize(payload) : payload).GetRawText()); }
        catch (ArgumentException ex) { throw new FactIngestException(ex.Message); }
    }

    internal static string? String(JsonElement payload, string property) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static string EventName(InputEventType type) => type switch { InputEventType.KeyDown => "keyDown", InputEventType.MouseButton => "mouseButton", InputEventType.MouseScroll => "mouseScroll", _ => throw new FactIngestException("Unknown input event type.") };
}
