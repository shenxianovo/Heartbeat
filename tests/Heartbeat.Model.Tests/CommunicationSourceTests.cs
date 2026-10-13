using System.Text.Json;
using Heartbeat.Application.Entities;
using Heartbeat.Observers.Sources;
using Heartbeat.Observers.Sources.Communication;

namespace Heartbeat.Model.Tests;

public sealed class CommunicationSourceTests
{
    [Test]
    [Property("caseId", "A12")]
    public async Task GraphNativeDeltaPreservesDraftRevisionIdentityAndFolderRemoval()
    {
        var records = await Fixture(new GraphMessagesSource(), "graph-delta.json");
        await Assert.That(records.Count).IsEqualTo(3);
        await Assert.That(records.Select(record => record.NativeKey).Distinct().Count()).IsEqualTo(1);
        await Assert.That(records.Select(record => record.EvidenceKey).Distinct().Count()).IsEqualTo(3);
        await Assert.That(records[0].Properties.GetProperty("subject").GetString()).IsEqualTo("Draft outline");
        await Assert.That(records[1].Properties.GetProperty("subject").GetString()).IsEqualTo("Draft outline revised");
        await Assert.That(records[0].Properties.GetProperty("isDraft").GetBoolean()).IsTrue();
        await Assert.That(records[0].Properties.GetProperty("flagStatus").GetString()).IsEqualTo("notFlagged");
        await Assert.That(records[1].Properties.GetProperty("flagStatus").GetString()).IsEqualTo("flagged");
        await Assert.That(records[2].Properties.GetProperty("operation").GetString()).IsEqualTo("removed-from-collection");
        await Assert.That(records[2].StartAt is null && records[2].EndAt is null).IsTrue();
        await Assert.That(records[0].StartAt).IsEqualTo(DateTimeOffset.Parse("2026-10-10T12:34:56Z", System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(records[1].Properties.GetProperty("bodyContent").GetString()).IsEqualTo("<p>After edit</p>");
    }

    [Test]
    public async Task GraphSenderAndFromAreDistinctAndHtmlStaysLiteral()
    {
        var records = await Fixture(new GraphMessagesSource(), "graph-delta.json");
        await Assert.That(records[0].Properties.GetProperty("fromAddress").GetString()).IsEqualTo("sender@example.invalid");
        await Assert.That(records[0].Properties.GetProperty("senderAddress").GetString()).IsEqualTo("delegate@example.invalid");
        await Assert.That(records[0].Properties.GetProperty("bodyContent").GetString()).IsEqualTo("Before edit\nUnicode: café 🛰️");
        await Assert.That(records[0].Properties.GetProperty("ccAddresses").GetArrayLength()).IsEqualTo(0);
        await Assert.That(records[0].Properties.TryGetProperty("bccAddresses", out _)).IsFalse();
    }

    [Test]
    public async Task GraphNativeChangeKeyReplaysAcrossCapturesButUnversionedTombstoneNeedsCapture()
    {
        var first = await Fixture(new GraphMessagesSource(), "graph-delta.json", capture: "capture-a");
        var second = await Fixture(new GraphMessagesSource(), "graph-delta.json", capture: "capture-b");
        await Assert.That(first[0].EvidenceKey).IsEqualTo(second[0].EvidenceKey);
        await Assert.That(first[1].EvidenceKey).IsEqualTo(second[1].EvidenceKey);
        await Assert.That(first[2].EvidenceKey == second[2].EvidenceKey).IsFalse();
        var replay = await Fixture(new GraphMessagesSource(), "graph-delta.json", capture: "capture-a");
        await Assert.That(first[2].EvidenceKey).IsEqualTo(replay[2].EvidenceKey);
    }

    [Test]
    public async Task GraphPageOrderingAndPropertyOrderingDoNotChangeEvidence()
    {
        var first = await Parse(new GraphMessagesSource(), """{"value":[{"id":"a","changeKey":"v1","subject":"A"},{"id":"b","changeKey":"v2"}]}""");
        var second = await Parse(new GraphMessagesSource(), """{"value":[{"changeKey":"v2","id":"b"},{"subject":"A","changeKey":"v1","id":"a"}]}""");
        await Assert.That(first[0].EvidenceKey).IsEqualTo(second[1].EvidenceKey);
        await Assert.That(first[1].EvidenceKey).IsEqualTo(second[0].EvidenceKey);
    }

    [Test]
    public async Task GraphUnknownRevisionTimeDoesNotUseReceiptOrCaptureTime()
    {
        var records = await Parse(new GraphMessagesSource(), """{"id":"a","receivedDateTime":"2026-10-01T00:00:00Z","sentDateTime":"2026-09-30T00:00:00Z"}""");
        await Assert.That(records[0].StartAt is null && records[0].EndAt is null).IsTrue();
        await Assert.That(records[0].TimeZone is null).IsTrue();
    }

    [Test]
    public async Task GraphFieldOmissionAndExplicitNullRemainDifferent()
    {
        var records = await Parse(new GraphMessagesSource(), """{"value":[{"id":"a"},{"id":"b","subject":null,"body":null,"isDraft":null}]}""");
        await Assert.That(records[0].Properties.TryGetProperty("subject", out _)).IsFalse();
        await Assert.That(records[1].Properties.GetProperty("subject").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(records[1].Properties.GetProperty("bodyContent").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(records[1].Properties.GetProperty("isDraft").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task GraphIdentityIsAccountScopedAndNeverUsesInternetMessageId()
    {
        const string json = """{"value":[{"id":"A","internetMessageId":"same"},{"id":"a","internetMessageId":"same"}]}""";
        var first = await Parse(new GraphMessagesSource(), json, "account-a");
        var second = await Parse(new GraphMessagesSource(), json, "account-b");
        await Assert.That(first[0].NativeKey == first[1].NativeKey).IsFalse();
        await Assert.That(first[0].NativeKey == second[0].NativeKey).IsFalse();
    }

    [Test]
    public async Task GraphDoesNotPersistDeltaUrlsOrEnvelopeCredentials()
    {
        var records = await Parse(new GraphMessagesSource(), """{"@odata.deltaLink":"https://example.invalid/?token=secret","access_token":"secret","value":[{"id":"a"}]}""");
        await Assert.That(records[0].Properties.GetRawText().Contains("secret", StringComparison.Ordinal)).IsFalse();
        await Assert.That(records[0].EvidenceKey.Contains("secret", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("[]")]
    [Arguments("{\"value\":{}}")]
    [Arguments("{\"value\":[null]}")]
    [Arguments("{\"id\":\"a\",\"id\":\"b\"}")]
    [Arguments("{\"id\":\"a\",\"\\u0069d\":\"b\"}")]
    [Arguments("{\"id\":\"a\",\"subject\":12}")]
    [Arguments("{\"id\":\"a\",\"isDraft\":\"true\"}")]
    [Arguments("{\"id\":\"a\",\"@removed\":true}")]
    [Arguments("{\"id\":\"a\",\"lastModifiedDateTime\":\"2026-10-12T12:00:00\"}")]
    [Arguments("{\"id\":\"a\",\"lastModifiedDateTime\":\"garbage\"}")]
    [Arguments("{\"id\":\"a\",\"lastModifiedDateTime\":\"2026-10-12Z\"}")]
    [Arguments("{\"id\":\"a\",\"lastModifiedDateTime\":\"2026-10-12 12:00:00Z\"}")]
    [Arguments("{\"id\":\"a\",\"body\":true}")]
    [Arguments("{\"id\":\"a\",\"toRecipients\":[{}]}")]
    public async Task GraphRejectsMalformedNativeData(string json)
    {
        await Assert.That(await Rejected(new GraphMessagesSource(), json)).IsTrue();
    }

    [Test]
    public async Task SlackNativeCreateEditDeleteRetainMessageIdentityAndHistory()
    {
        var created = (await Fixture(new SlackEventsSource(), "slack-create.json"))[0];
        var edited = (await Fixture(new SlackEventsSource(), "slack-edit.json"))[0];
        var deleted = (await Fixture(new SlackEventsSource(), "slack-delete.json"))[0];
        await Assert.That(created.NativeKey).IsEqualTo(edited.NativeKey);
        await Assert.That(edited.NativeKey).IsEqualTo(deleted.NativeKey);
        await Assert.That(new[] { created.EvidenceKey, edited.EvidenceKey, deleted.EvidenceKey }.Distinct().Count()).IsEqualTo(3);
        await Assert.That(created.Properties.GetProperty("text").GetString()).IsEqualTo("Before edit café 🛰️");
        await Assert.That(edited.Properties.GetProperty("text").GetString()).IsEqualTo("After edit");
        await Assert.That(edited.Properties.GetProperty("previousText").GetString()).IsEqualTo("Before edit café 🛰️");
        await Assert.That(deleted.Properties.GetProperty("previousText").GetString()).IsEqualTo("After edit");
        await Assert.That(deleted.Properties.TryGetProperty("text", out _)).IsFalse();
        await Assert.That(deleted.Properties.GetProperty("operation").GetString()).IsEqualTo("delete");
    }

    [Test]
    public async Task SlackFactTimesPreserveMicrosecondsAndNeverUseDispatchTime()
    {
        var created = (await Fixture(new SlackEventsSource(), "slack-create.json"))[0];
        var edited = (await Fixture(new SlackEventsSource(), "slack-edit.json"))[0];
        var deleted = (await Fixture(new SlackEventsSource(), "slack-delete.json"))[0];
        await Assert.That(created.StartAt).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1791635696).AddTicks(1234560));
        await Assert.That(edited.StartAt).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1791635756).AddTicks(6543210));
        await Assert.That(deleted.StartAt).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1791635856).AddTicks(10));
        await Assert.That(created.StartAt == created.EndAt && edited.StartAt == edited.EndAt && deleted.StartAt == deleted.EndAt).IsTrue();
    }

    [Test]
    public async Task SlackNativeEventIdReplaysEvenWhenCaptureKeyChanges()
    {
        var first = (await Fixture(new SlackEventsSource(), "slack-edit.json", capture: "capture-a"))[0];
        var second = (await Fixture(new SlackEventsSource(), "slack-edit.json", capture: "capture-b"))[0];
        await Assert.That(first.EvidenceKey).IsEqualTo(second.EvidenceKey);
        await Assert.That(first.NativeKey).IsEqualTo(second.NativeKey);
    }

    [Test]
    public async Task SlackChannelWorkspaceAndAccountScopesPreventTsCollisions()
    {
        var json = await File.ReadAllTextAsync(FixturePath("slack-create.json"));
        var original = (await Parse(new SlackEventsSource(), json, "account-a"))[0];
        var channel = (await Parse(new SlackEventsSource(), json.Replace("CSYNTHETIC", "COTHER", StringComparison.Ordinal), "account-a"))[0];
        var workspace = (await Parse(new SlackEventsSource(), json.Replace("TSYNTHETIC", "TOTHER", StringComparison.Ordinal), "account-a"))[0];
        var account = (await Parse(new SlackEventsSource(), json, "account-b"))[0];
        await Assert.That(new[] { original.NativeKey, channel.NativeKey, workspace.NativeKey, account.NativeKey }.Distinct().Count()).IsEqualTo(4);
        await Assert.That(original.References["threadRootId"].Kind).IsEqualTo("slack-message");
        await Assert.That(original.References["threadRootId"].NativeKey == original.NativeKey).IsFalse();
    }

    [Test]
    public async Task SlackUnknownDeletionTimeRemainsUnknownDespiteEnvelopeDispatchTime()
    {
        var records = await Parse(new SlackEventsSource(), """{"type":"event_callback","team_id":"T","event_id":"E","event_time":1791635900,"event":{"type":"message","subtype":"message_deleted","channel":"C","deleted_ts":"1791635696.123456"}}""");
        await Assert.That(records[0].StartAt is null && records[0].EndAt is null).IsTrue();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("{\"type\":\"url_verification\",\"challenge\":\"test\"}")]
    [Arguments("{\"type\":\"event_callback\",\"team_id\":\"T\",\"event_id\":\"E\",\"event\":{\"type\":\"reaction_added\"}}")]
    [Arguments("{\"type\":\"event_callback\",\"team_id\":\"T\",\"event_id\":\"E\",\"event\":{\"type\":\"message\",\"channel\":\"C\",\"subtype\":\"bot_message\",\"ts\":\"1.000001\"}}")]
    [Arguments("{\"type\":\"event_callback\",\"team_id\":\"T\",\"event_id\":\"E\",\"event\":{\"type\":\"message\",\"channel\":\"C\",\"ts\":\"NaN\"}}")]
    [Arguments("{\"type\":\"event_callback\",\"team_id\":\"T\",\"event_id\":\"E\",\"event\":{\"type\":\"message\",\"channel\":\"C\",\"ts\":\"-1\"}}")]
    [Arguments("{\"type\":\"event_callback\",\"team_id\":\"T\",\"event_id\":\"E\",\"event\":{\"type\":\"message\",\"channel\":\"C\",\"subtype\":\"message_changed\",\"message\":null}}")]
    public async Task SlackRejectsUnsupportedOrMalformedEvents(string json)
    {
        await Assert.That(await Rejected(new SlackEventsSource(), json)).IsTrue();
    }

    [Test]
    public async Task NativeSchemasAndEveryFixtureRecordPassActualModelValidation()
    {
        foreach (var (source, fixture) in new (IRecordSource, string)[]
        {
            (new GraphMessagesSource(), "graph-delta.json"), (new SlackEventsSource(), "slack-create.json"),
            (new SlackEventsSource(), "slack-edit.json"), (new SlackEventsSource(), "slack-delete.json")
        })
        {
            foreach (var record in await Fixture(source, fixture))
            {
                var references = record.References.ToDictionary(pair => pair.Key,
                    _ => "019a0000-0000-7000-8000-000000009999", StringComparer.Ordinal);
                references.Add("sourceEntityId", "019a0000-0000-7000-8000-000000009998");
                await Assert.That(EntitySchemaRules.ValidFields(record.Schema.Fields)).IsTrue();
                await Assert.That(EntitySchemaRules.ValidEntity(record.Schema.Fields,
                    JsonSerializer.SerializeToElement(references), record.Properties)).IsTrue();
            }
        }
    }

    [Test]
    public async Task NativeDecodedStringLimitRejectsRatherThanTruncates()
    {
        var json = JsonSerializer.Serialize(new { id = "a", subject = new string('x', 262145) });
        await Assert.That(await Rejected(new GraphMessagesSource(), json)).IsTrue();
    }

    [Test]
    public async Task NativeFileLimitRejectsOversizedInput()
    {
        var path = Path.GetTempFileName();
        try
        {
            await using (var stream = File.OpenWrite(path)) stream.SetLength(8 * 1024 * 1024 + 1);
            var rejected = false;
            try { _ = await Collect(new GraphMessagesSource(), path); }
            catch (InvalidDataException) { rejected = true; }
            await Assert.That(rejected).IsTrue();
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task NativeImportHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = false;
        try
        {
            await foreach (var _ in new GraphMessagesSource().ReadAsync(
                new SourceReadContext(FixturePath("graph-delta.json"), "account", "capture"), cancellation.Token)) { }
        }
        catch (OperationCanceledException) { canceled = true; }
        await Assert.That(canceled).IsTrue();
    }

    [Test]
    [Property("caseId", "A11")]
    public async Task GraphImmutableIdSurvivesFolderChangeButDefaultReplacementIsNotGuessed()
    {
        var records = await Parse(new GraphMessagesSource(), """{"value":[{"id":"immutable-a","parentFolderId":"inbox","changeKey":"v1"},{"id":"immutable-a","parentFolderId":"archive","changeKey":"v2"},{"id":"different-default-id","parentFolderId":"archive","changeKey":"v2"}]}""");
        await Assert.That(records[0].NativeKey).IsEqualTo(records[1].NativeKey);
        await Assert.That(records[0].References["parentFolderId"] == records[1].References["parentFolderId"]).IsFalse();
        await Assert.That(records[1].NativeKey == records[2].NativeKey).IsFalse();
    }

    [Test]
    [Property("caseId", "A13")]
    public async Task SlackOutOfOrderEditAndCreationKeepHistoricalContentThroughRestart()
    {
        var created = (await Fixture(new SlackEventsSource(), "slack-create.json"))[0];
        var edited = (await Fixture(new SlackEventsSource(), "slack-edit.json"))[0];
        var directory = Path.Combine(Path.GetTempPath(), "heartbeat-communication-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");
        SourceReceipt original;
        SourceReceipt revision;
        try
        {
            using (var store = new SourceStore(path))
            {
                revision = store.Enqueue("account", edited);
                original = store.Enqueue("account", created);
                await Assert.That(original.SourceEntityId).IsEqualTo(revision.SourceEntityId);
                await Assert.That(original.ContentId == revision.ContentId).IsFalse();
                await Assert.That(original.ObservationId == revision.ObservationId).IsFalse();
                var oldContent = store.Pending().SelectMany(item => item.Entities).Single(item => item.Id == original.ContentId);
                await Assert.That(oldContent.Body.GetProperty("properties").GetProperty("text").GetString()).IsEqualTo("Before edit café 🛰️");
            }
            using (var reopened = new SourceStore(path))
            {
                var replay = reopened.Enqueue("account", created);
                await Assert.That(replay.Replayed).IsTrue();
                await Assert.That(replay.ObservationId).IsEqualTo(original.ObservationId);
                await Assert.That(reopened.Pending().Count).IsEqualTo(2);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    [Property("caseId", "A14")]
    public async Task SlackDeleteWithoutCapturedOriginalDoesNotInventText()
    {
        var records = await Parse(new SlackEventsSource(), """{"type":"event_callback","team_id":"T","event_id":"E","event":{"type":"message","subtype":"message_deleted","channel":"C","deleted_ts":"1791635696.123456","event_ts":"1791635856.000001"}}""");
        await Assert.That(records.Count).IsEqualTo(1);
        await Assert.That(records[0].Properties.TryGetProperty("text", out _)).IsFalse();
        await Assert.That(records[0].Properties.TryGetProperty("previousText", out _)).IsFalse();
        await Assert.That(records[0].Properties.GetProperty("operation").GetString()).IsEqualTo("delete");
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "NativeFixtures", "Communication", name);
    private static Task<List<SourceRecord>> Fixture(IRecordSource source, string name, string scope = "account", string capture = "capture") =>
        Collect(source, FixturePath(name), scope, capture);

    private static async Task<List<SourceRecord>> Parse(IRecordSource source, string json, string scope = "account")
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, json);
            return await Collect(source, path, scope);
        }
        finally { File.Delete(path); }
    }

    private static async Task<bool> Rejected(IRecordSource source, string json)
    {
        try { _ = await Parse(source, json); return false; }
        catch (InvalidDataException) { return true; }
        catch (JsonException) { return true; }
    }

    private static async Task<List<SourceRecord>> Collect(IRecordSource source, string path, string scope = "account", string capture = "capture")
    {
        var records = new List<SourceRecord>();
        await foreach (var record in source.ReadAsync(new SourceReadContext(path, scope, capture, DateTimeOffset.UtcNow), CancellationToken.None))
            records.Add(record);
        return records;
    }
}
