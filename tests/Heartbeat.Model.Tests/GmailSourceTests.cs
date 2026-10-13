using System.Text.Json;
using Heartbeat.Application.Entities;
using Heartbeat.Observers.Sources;
using Heartbeat.Observers.Sources.Gmail;

namespace Heartbeat.Model.Tests;

public sealed class GmailSourceTests
{
    [Test]
    [Property("caseId", "A10")]
    public async Task NativeMessagePreservesMimeMembershipHeadersAndUndownloadedAttachment()
    {
        var record = (await Fixture(new GmailMessagesSource(), "message.json"))[0];
        var parts = record.Properties.GetProperty("mimeParts");
        await Assert.That(parts.GetArrayLength()).IsEqualTo(5);
        await Assert.That(parts[2].GetProperty("parentPath").GetString()).IsEqualTo("0.0");
        await Assert.That(parts[2].GetProperty("bodyData").GetString()).IsEqualTo("aGVsbG8");
        await Assert.That(parts[0].GetProperty("headers")[2].GetProperty("value").GetString()).IsEqualTo("hop-one");
        await Assert.That(parts[0].GetProperty("headers")[3].GetProperty("value").GetString()).IsEqualTo("hop-two");
        await Assert.That(parts[4].GetProperty("attachmentId").GetString()).IsEqualTo("attachment-a");
        await Assert.That(parts[4].TryGetProperty("bodyData", out _)).IsFalse();
        await Assert.That(record.Properties.GetProperty("labelIds")[1].GetString()).IsEqualTo("UNREAD");
        await Assert.That(record.Properties.GetProperty("internalDate").GetString()).IsEqualTo("1791547200123");
        await Assert.That(record.StartAt is null && record.EndAt is null && record.TimeZone is null).IsTrue();
    }

    [Test]
    [Property("caseId", "A09")]
    public async Task NativeDraftReplacedMessageKeepsDraftIdentityAndHistoryAcrossRestart()
    {
        var first = (await Fixture(new GmailDraftsSource(), "draft-before.json"))[0];
        var second = (await Fixture(new GmailDraftsSource(), "draft-after.json"))[0];
        await Assert.That(first.NativeKey).IsEqualTo(second.NativeKey);
        await Assert.That(first.References["messageEntityId"] == second.References["messageEntityId"]).IsFalse();
        var directory = Path.Combine(Path.GetTempPath(), "heartbeat-gmail-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            SourceReceipt original;
            using (var store = new SourceStore(Path.Combine(directory, "state.db")))
            {
                var newer = store.Enqueue("account-a", second);
                original = store.Enqueue("account-a", first);
                await Assert.That(original.SourceEntityId).IsEqualTo(newer.SourceEntityId);
                await Assert.That(original.ContentId == newer.ContentId).IsFalse();
                await Assert.That(original.ObservationId == newer.ObservationId).IsFalse();
                await Assert.That(store.Pending().Count).IsEqualTo(2);
            }
            using var reopened = new SourceStore(Path.Combine(directory, "state.db"));
            var replay = reopened.Enqueue("account-a", first);
            await Assert.That(replay.Replayed).IsTrue();
            await Assert.That(replay.ObservationId).IsEqualTo(original.ObservationId);
            await Assert.That(reopened.Pending().Count).IsEqualTo(2);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    [Property("caseId", "A08")]
    public async Task NativeHistoryKeepsSpecificChangesUnknownTimeAndIncompletePage()
    {
        var records = await Fixture(new GmailHistorySource(), "history.json");
        await Assert.That(records.Count).IsEqualTo(6);
        await Assert.That(records.Count(r => r.Properties.TryGetProperty("messageId", out var id) && id.GetString() == "message-a")).IsEqualTo(2);
        await Assert.That(records[0].Properties.GetProperty("operation").GetString()).IsEqualTo("labelsAdded");
        await Assert.That(records[1].Properties.GetProperty("operation").GetString()).IsEqualTo("labelsRemoved");
        await Assert.That(records[3].Properties.GetProperty("operation").GetString()).IsEqualTo("messagesDeleted");
        await Assert.That(records[4].Properties.GetProperty("operation").GetString()).IsEqualTo("unspecified-change");
        await Assert.That(records.All(r => r.StartAt is null && r.EndAt is null)).IsTrue();
        await Assert.That(records[^1].Properties.GetProperty("completeness").GetString()).IsEqualTo("incomplete-page-sequence");
        await Assert.That(records[^1].Properties.GetProperty("continuationFetched").GetBoolean()).IsFalse();
        await Assert.That(records.Any(r => r.Properties.GetRawText().Contains("fixture-continuation-token", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task AllNativeGmailSchemasValidateAndEnqueueWithReservedIdentity()
    {
        var records = new List<SourceRecord>();
        records.AddRange(await Fixture(new GmailMessagesSource(), "message.json"));
        records.AddRange(await Fixture(new GmailDraftsSource(), "draft-before.json"));
        records.AddRange(await Fixture(new GmailHistorySource(), "history.json"));
        var directory = Path.Combine(Path.GetTempPath(), "heartbeat-gmail-schema-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new SourceStore(Path.Combine(directory, "state.db"));
            foreach (var record in records)
            {
                await Assert.That(EntitySchemaRules.ValidFields(record.Schema.Fields)).IsTrue();
                var references = record.References.ToDictionary(pair => pair.Key, _ => Guid.Parse("019a0000-0000-7000-8000-000000000999"));
                references.Add("sourceEntityId", Guid.Parse("019a0000-0000-7000-8000-000000000998"));
                await Assert.That(EntitySchemaRules.ValidEntity(record.Schema.Fields, SourceFields.Of(references), record.Properties)).IsTrue();
                _ = store.Enqueue("account-a", record);
            }
            await Assert.That(store.EnqueueBatch("account-a", records).All(r => r.Replayed)).IsTrue();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task AccountScopeAndCaptureProvenanceSeparateIndependentSnapshots()
    {
        var source = new GmailMessagesSource();
        var first = (await Fixture(source, "message.json"))[0];
        var otherAccount = (await Fixture(source, "message.json", "account-b"))[0];
        var anotherCapture = (await Fixture(source, "message.json", capture: "next"))[0];
        await Assert.That(first.NativeKey == otherAccount.NativeKey).IsFalse();
        await Assert.That(first.EvidenceKey == anotherCapture.EvidenceKey).IsFalse();
        await Assert.That(first.NativeKey).IsEqualTo(anotherCapture.NativeKey);
    }

    [Test]
    public async Task HistoryNativeEventsReplayAcrossCapturesWithoutInventingCursorExpiry()
    {
        var first = await Fixture(new GmailHistorySource(), "history.json");
        var second = await Fixture(new GmailHistorySource(), "history.json", capture: "next");
        await Assert.That(first[0].EvidenceKey).IsEqualTo(second[0].EvidenceKey);
        var terminal = await Parse(new GmailHistorySource(), """{"historyId":"1000"}""");
        await Assert.That(terminal.Count).IsEqualTo(1);
        await Assert.That(terminal[0].Properties.GetProperty("completeness").GetString()).IsEqualTo("terminal-page-only");
        var rejected = false;
        try { _ = await Fixture(new GmailHistorySource(), "history-error.json"); }
        catch (InvalidDataException) { rejected = true; }
        await Assert.That(rejected).IsTrue();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("[]")]
    [Arguments("{\"id\":\"x\",\"\\u0069d\":\"y\"}")]
    [Arguments("{\"id\":\"x\",\"internalDate\":\"tomorrow\"}")]
    [Arguments("{\"id\":\"x\",\"sizeEstimate\":\"12\"}")]
    [Arguments("{\"id\":\"x\",\"labelIds\":[1]}")]
    [Arguments("{\"id\":\"x\",\"payload\":{\"body\":{\"data\":\"a!\"}}}")]
    [Arguments("{\"id\":\"x\",\"payload\":{\"headers\":{}}}")]
    public async Task MalformedNativeMessagesFail(string json)
    {
        var rejected = false;
        try { _ = await Parse(new GmailMessagesSource(), json); }
        catch (InvalidDataException) { rejected = true; }
        catch (JsonException) { rejected = true; }
        await Assert.That(rejected).IsTrue();
    }

    [Test]
    public async Task WorkspaceClassificationLabelsRemainDistinctFromMailboxLabels()
    {
        var records = await Parse(new GmailMessagesSource(), """{"id":"m","labelIds":["INBOX"],"classificationLabelValues":[{"labelId":"policy-label","fields":[{"fieldId":"sensitivity","selection":"confidential"}]}]}""");
        var labels = records[0].Properties.GetProperty("classificationLabelValues");
        await Assert.That(labels[0].GetProperty("fields")[0].GetProperty("selection").GetString()).IsEqualTo("confidential");
        var references = SourceFields.Of(new { sourceEntityId = Guid.Parse("019a0000-0000-7000-8000-000000000999") });
        await Assert.That(EntitySchemaRules.ValidEntity(records[0].Schema.Fields, references, records[0].Properties)).IsTrue();
    }

    [Test]
    [Arguments("{\"id\":\"m\",\"newField\":\"unmapped\"}")]
    [Arguments("{\"id\":\"m\",\"payload\":{\"body\":{\"newField\":1}}}")]
    public async Task UnsupportedMessageFieldsRejectRatherThanSilentlyLoseData(string json)
    {
        var rejected = false;
        try { _ = await Parse(new GmailMessagesSource(), json); }
        catch (InvalidDataException) { rejected = true; }
        await Assert.That(rejected).IsTrue();
    }

    [Test]
    public async Task HistoryPartialMessageProjectionCanGainThreadWithoutEvidenceCollision()
    {
        var first = (await Parse(new GmailHistorySource(), """{"historyId":"100","history":[{"id":"90","messagesAdded":[{"message":{"id":"m"}}]}]}"""))[0];
        var later = (await Parse(new GmailHistorySource(), """{"historyId":"100","history":[{"id":"90","messagesAdded":[{"message":{"id":"m","threadId":"t"}}]}]}"""))[0];
        await Assert.That(first.NativeKey).IsEqualTo(later.NativeKey);
        await Assert.That(first.EvidenceKey == later.EvidenceKey).IsFalse();
        var directory = Path.Combine(Path.GetTempPath(), "heartbeat-gmail-projection-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new SourceStore(Path.Combine(directory, "state.db"));
            var receipts = store.EnqueueBatch("account-a", [first, later]);
            await Assert.That(receipts[0].SourceEntityId).IsEqualTo(receipts[1].SourceEntityId);
            await Assert.That(receipts[0].ContentId == receipts[1].ContentId).IsFalse();
            await Assert.That(store.Enqueue("account-a", later).Replayed).IsTrue();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task CancelledImportDoesNotYieldRecords()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cancelled = false;
        try
        {
            await foreach (var record in new GmailMessagesSource().ReadAsync(new(FixturePath("message.json"), "account-a", "capture"), cancellation.Token))
                _ = record;
        }
        catch (OperationCanceledException) { cancelled = true; }
        await Assert.That(cancelled).IsTrue();
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "NativeFixtures", "Gmail", name);
    private static Task<List<SourceRecord>> Fixture(IRecordSource source, string name, string scope = "account-a", string capture = "capture") => Collect(source, FixturePath(name), scope, capture);
    private static async Task<List<SourceRecord>> Parse(IRecordSource source, string json)
    {
        var path = Path.GetTempFileName();
        try { await File.WriteAllTextAsync(path, json); return await Collect(source, path, "account-a", "capture"); }
        finally { File.Delete(path); }
    }
    private static async Task<List<SourceRecord>> Collect(IRecordSource source, string path, string scope, string capture)
    {
        var records = new List<SourceRecord>();
        await foreach (var record in source.ReadAsync(new(path, scope, capture, DateTimeOffset.UtcNow), CancellationToken.None)) records.Add(record);
        return records;
    }
}
