using System.Text.Json;
using Heartbeat.Application.Entities;
using Heartbeat.Observers.Sources;
using Heartbeat.Observers.Sources.PhysicalCalendar;

namespace Heartbeat.Model.Tests;

public sealed class PhysicalCalendarSourceTests
{
    [Test]
    [Property("caseId", "C11")]
    public async Task CalendarAllDayDatesStayDatesWithoutInventedInstants()
    {
        var records = await Fixture(new ICalendarSource(), "calendar.ics");
        await Assert.That(records.Count).IsEqualTo(6);
        await Assert.That(records[0].Properties.GetProperty("startKind").GetString()).IsEqualTo("date");
        await Assert.That(records[0].Properties.GetProperty("end").GetString()).IsEqualTo("20261012");
        await Assert.That(records[0].StartAt is null && records[0].EndAt is null && records[0].TimeZone is null).IsTrue();
        await Assert.That(records[5].StartAt).IsEqualTo(DateTimeOffset.Parse("2026-10-12T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(records[5].EndAt).IsEqualTo(DateTimeOffset.Parse("2026-10-12T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    [Property("caseId", "C12")]
    public async Task CalendarRecurrenceIdentityUsesOriginalStartNotMovedStart()
    {
        var records = await Fixture(new ICalendarSource(), "calendar.ics");
        var moved = records[2];
        await Assert.That(moved.NativeKey == records[1].NativeKey).IsFalse();
        await Assert.That(moved.Properties.GetProperty("recurrenceId").GetString()).IsEqualTo("20261108T013000");
        await Assert.That(moved.Properties.GetProperty("start").GetString()).IsEqualTo("20261108T023000");
        await Assert.That(moved.StartAt is null && moved.TimeZone is null).IsTrue();
        await Assert.That(records[1].Properties.GetProperty("recurrenceRules")[0].GetString()).IsEqualTo("RRULE:FREQ=WEEKLY;COUNT=3");
        await Assert.That(records[1].Properties.GetProperty("exclusionDates")[0].GetString()).IsEqualTo("EXDATE;TZID=America/New_York:20261108T013000");
        var source = await File.ReadAllTextAsync(PathFor("calendar.ics"));
        var revision = (await Parse(new ICalendarSource(), source.Replace("20261108T023000", "20261108T033000").Replace("SEQUENCE:1", "SEQUENCE:3")))[2];
        await Assert.That(moved.NativeKey).IsEqualTo(revision.NativeKey);
        await Assert.That(moved.EvidenceKey == revision.EvidenceKey).IsFalse();
    }

    [Test]
    [Property("caseId", "C13")]
    public async Task CalendarFloatingAndFoldedEscapedTextRemainNative()
    {
        var record = (await Fixture(new ICalendarSource(), "calendar.ics"))[3];
        await Assert.That(record.Properties.GetProperty("startKind").GetString()).IsEqualTo("floating");
        await Assert.That(record.Properties.GetProperty("summary").GetString()).IsEqualTo("Café, planningcontinued");
        await Assert.That(record.Properties.GetProperty("description").GetString()).IsEqualTo("First line\nSecond line");
        await Assert.That(record.StartAt is null && record.TimeZone is null).IsTrue();
    }

    [Test]
    [Property("caseId", "C14")]
    public async Task CalendarCancellationDoesNotBecomeDeletionOrImportTime()
    {
        var record = (await Fixture(new ICalendarSource(), "calendar.ics"))[4];
        await Assert.That(record.Properties.GetProperty("status").GetString()).IsEqualTo("CANCELLED");
        await Assert.That(record.Properties.GetProperty("recurrenceId").GetString()).IsEqualTo("20261115T013000");
        await Assert.That(record.StartAt is null && record.EndAt is null).IsTrue();
    }

    [Test]
    [Arguments("UID:a\nUID:b\nDTSTAMP:20261010T080000Z")]
    [Arguments("UID:a\nDTSTAMP:20261010T080000Z\nDTSTART;TZID=A;TZID=B:20261010T080000")]
    [Arguments("UID:a\nDTSTAMP:20261010T080000Z\nDTSTART;TZID=A:20261010T080000Z")]
    [Arguments("UID:a\nDTSTAMP:20261010T080000Z\nDTSTART;VALUE=DATE:20260230")]
    [Arguments("UID:a\nDTSTAMP:20261010T080000Z\nSEQUENCE:-1")]
    [Arguments("UID:a\nDTSTAMP:20261010T080000Z\nDTSTART;VALUE=DATE:20261012\nDTEND;VALUE=DATE:20261011")]
    [Arguments("UID:a\nDTSTAMP:20261010T080000Z\nRRULE:FREQ=DAILY\nRRULE:FREQ=WEEKLY")]
    [Arguments("UID:a\nDTSTAMP:20261010T080000")]
    [Arguments("UID:a\nDTSTAMP:20261010T080000Z\nDTSTART:20261010T080000Z\nDTEND:20261010T090000Z\nDURATION:PT1H")]
    public async Task CalendarRejectsAmbiguousOrInvalidMappedFields(string body) =>
        await Assert.That(await Rejected(new ICalendarSource(), Calendar(body))).IsTrue();

    [Test]
    public async Task CalendarRejectsDuplicateEventIdentityRatherThanLastWriterWins()
    {
        var body = "BEGIN:VEVENT\nUID:a\nDTSTAMP:20261010T080000Z\nEND:VEVENT\n";
        await Assert.That(await Rejected(new ICalendarSource(), "BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:test\n" + body + body + "END:VCALENDAR")).IsTrue();
    }

    [Test]
    [Property("caseId", "C03")]
    public async Task GpxPreservesBackwardClockAndRepeatedIdenticalPoints()
    {
        var records = await Fixture(new GpxSource(), "track.gpx");
        var points = records.Where(r => r.Kind == "gpx-point").ToArray();
        await Assert.That(points.Length).IsEqualTo(6);
        await Assert.That(points[1].NativeKey == points[2].NativeKey).IsFalse();
        await Assert.That(points[1].StartAt).IsEqualTo(points[2].StartAt);
        await Assert.That(points[3].StartAt < points[2].StartAt).IsTrue();
        await Assert.That(points[0].StartAt is null && points[5].StartAt is null).IsTrue();
        await Assert.That(points[0].Properties.GetProperty("hdop").GetDecimal()).IsEqualTo(1.2m);
        await Assert.That(points[0].Properties.TryGetProperty("accuracyMeters", out _)).IsFalse();
        await Assert.That(points[4].StartAt).IsEqualTo(DateTimeOffset.Parse("2026-10-10T13:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(points.All(p => p.TimeZone is null)).IsTrue();
    }

    [Test]
    public async Task GpxSubTickPrecisionTruncatesWithoutCrossingSecondAndRetainsOriginal()
    {
        const string exact = "2026-10-10T12:00:00.999999999Z";
        var record = (await Parse(new GpxSource(), Gpx("<wpt lat='0' lon='0'><time>" + exact + "</time></wpt>")))[0];
        await Assert.That(record.Properties.GetProperty("exactTime").GetString()).IsEqualTo(exact);
        await Assert.That(record.StartAt).IsEqualTo(DateTimeOffset.Parse("2026-10-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture).AddTicks(9999999));
    }

    [Test]
    public async Task GpxTrackSegmentMembershipIsARealReference()
    {
        var records = await Fixture(new GpxSource(), "track.gpx");
        var track = records.Single(r => r.Kind == "gpx-track");
        var segments = records.Where(r => r.Kind == "gpx-segment").ToArray();
        await Assert.That(segments.Length).IsEqualTo(2);
        await Assert.That(segments.All(s => s.References["trackId"].NativeKey == track.NativeKey)).IsTrue();
        await Assert.That(records.Count(r => r.References.TryGetValue("segmentId", out var reference) && reference.NativeKey == segments[0].NativeKey)).IsEqualTo(3);
    }

    [Test]
    [Arguments("<wpt lat='91' lon='0'/>")]
    [Arguments("<wpt lat='0' lon='180'/>")]
    [Arguments("<wpt lat='NaN' lon='0'/>")]
    [Arguments("<wpt lat='0' lon='0'><time>bad</time></wpt>")]
    [Arguments("<wpt lat='0' lon='0'><ele>1</ele><ele>2</ele></wpt>")]
    [Arguments("<wpt lat='0' lon='0'><hdop>-1</hdop></wpt>")]
    [Arguments("<wpt lat='0' lon='0'><sat>-1</sat></wpt>")]
    [Arguments("<rte/>")]
    public async Task GpxRejectsInvalidOrUnsupportedNativeData(string body) =>
        await Assert.That(await Rejected(new GpxSource(), Gpx(body))).IsTrue();

    [Test]
    public async Task GpxProhibitsDtdAndExternalEntities()
    {
        await Assert.That(await Rejected(new GpxSource(), "<!DOCTYPE gpx [<!ENTITY leak SYSTEM 'file:///etc/passwd'>]>" + Gpx("<wpt lat='0' lon='0'><name>&leak;</name></wpt>"))).IsTrue();
    }

    [Test]
    public async Task BothImportsReplayPersistentlyAndDifferentScopesDoNotCollide()
    {
        foreach (var (source, name) in Sources())
        {
            var first = await Fixture(source, name);
            var replay = await Fixture(source, name);
            var anotherCapture = await Fixture(source, name, capture: "second");
            var anotherScope = await Fixture(source, name, scope: "other");
            await Assert.That(first[0].NativeKey).IsEqualTo(replay[0].NativeKey);
            await Assert.That(first[0].EvidenceKey).IsEqualTo(replay[0].EvidenceKey);
            await Assert.That(first[0].NativeKey == anotherScope[0].NativeKey).IsFalse();
            await Assert.That(first[0].EvidenceKey == anotherCapture[0].EvidenceKey).IsFalse();
            if (source is GpxSource) await Assert.That(first[0].NativeKey == anotherCapture[0].NativeKey).IsFalse();
            var directory = Path.Combine(Path.GetTempPath(), "physical-source-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "state.db");
                SourceReceipt receipt;
                using (var store = new SourceStore(path)) receipt = store.Enqueue("scope", first[0]);
                using (var store = new SourceStore(path))
                {
                    var retry = store.Enqueue("scope", replay[0]);
                    await Assert.That(receipt.ObservationId).IsEqualTo(retry.ObservationId);
                    await Assert.That(receipt.SourceEntityId).IsEqualTo(retry.SourceEntityId);
                }
            }
            finally { Directory.Delete(directory, true); }
        }
    }

    [Test]
    public async Task AllPhysicalSchemasAndRecordsPassCurrentEntitySchemaRules()
    {
        foreach (var (source, name) in Sources())
            foreach (var record in await Fixture(source, name))
            {
                var references = record.References.ToDictionary(p => p.Key, _ => "019a0000-0000-7000-8000-000000009999");
                references.Add("sourceEntityId", "019a0000-0000-7000-8000-000000009998");
                await Assert.That(EntitySchemaRules.ValidFields(record.Schema.Fields)).IsTrue();
                await Assert.That(EntitySchemaRules.ValidEntity(record.Schema.Fields, JsonSerializer.SerializeToElement(references), record.Properties)).IsTrue();
            }
    }

    [Test]
    public async Task BothImportsRejectOversizeAndHonorCancellation()
    {
        var path = Path.GetTempFileName();
        try
        {
            await using (var file = File.OpenWrite(path)) file.SetLength(SourceJson.MaximumInputBytes + 1);
            foreach (var (source, name) in Sources())
            {
                var oversized = false;
                try { _ = await Collect(source, path); } catch (InvalidDataException) { oversized = true; }
                await Assert.That(oversized).IsTrue();
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                var canceled = false;
                try { _ = await Collect(source, PathFor(name), cancellation: cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
                await Assert.That(canceled).IsTrue();
            }
        }
        finally { File.Delete(path); }
    }

    private static (IRecordSource Source, string Name)[] Sources() => [(new ICalendarSource(), "calendar.ics"), (new GpxSource(), "track.gpx")];
    private static string PathFor(string name) => Path.Combine(AppContext.BaseDirectory, "NativeFixtures", "PhysicalCalendar", name);
    private static Task<List<SourceRecord>> Fixture(IRecordSource source, string name, string scope = "scope", string capture = "capture") => Collect(source, PathFor(name), scope, capture);
    private static async Task<List<SourceRecord>> Collect(IRecordSource source, string path, string scope = "scope", string capture = "capture", CancellationToken cancellation = default)
    {
        var records = new List<SourceRecord>();
        await foreach (var record in source.ReadAsync(new(path, scope, capture, DateTimeOffset.UtcNow), cancellation)) records.Add(record);
        return records;
    }
    private static async Task<List<SourceRecord>> Parse(IRecordSource source, string text)
    {
        var path = Path.GetTempFileName();
        try { await File.WriteAllTextAsync(path, text); return await Collect(source, path); }
        finally { File.Delete(path); }
    }
    private static async Task<bool> Rejected(IRecordSource source, string text)
    {
        try { _ = await Parse(source, text); return false; } catch (InvalidDataException) { return true; }
    }
    private static string Calendar(string body) => "BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:test\nBEGIN:VEVENT\n" + body + "\nEND:VEVENT\nEND:VCALENDAR";
    private static string Gpx(string body) => "<gpx xmlns='http://www.topografix.com/GPX/1/1' version='1.1' creator='test'>" + body + "</gpx>";
}
