using System.Net.Http.Json;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Runtime.Store;
using Orisumi.Observer.Sdk;

namespace Orisumi.Integration.Tests;

public sealed class RuntimeOutcomeTests
{
    [Test, Property("caseId", "runtime-handoff")]
    public async Task ReceiptTracksUnnamedVersionsAcrossRestartAndReportsDependencyRejectionAndDiscard()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        using var directory = new RuntimeHostingTests.RuntimeDirectory();
        var path = Path.Combine(directory.Data, "outcome.sqlite");
        var instance = EntityId.New();
        var schema = EntityApiTests.Schema();
        var batchId = Guid.CreateVersion7().ToString();
        var operations = JsonLines.Json(new { operations = new object[] {
            new { op = "draft", entityId = schema.EntityId.Value, category = schema.Category, content = VersionJson.Content(schema.Content), strategy = "OnCompletion" },
            new { op = "complete", entityId = schema.EntityId.Value } } });
        using (var store = new ObserverStore(path, instance, http))
        {
            var session = new LocalObserverSession(store);
            await session.RequestAsync("commit", operations, batchId);
            var pending = await session.RequestAsync("outcome", JsonLines.Json(new { batchId }));
            await Assert.That(pending.GetProperty("state").GetString()).IsEqualTo("pending");
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.That(async () => await session.RequestAsync("outcome", JsonLines.Json(new { batchId, wait = 30 }), cancellationToken: cancelled.Token)).Throws<OperationCanceledException>();
            await store.SendAsync();
        }
        using (var store = new ObserverStore(path, instance, http))
        {
            var session = new LocalObserverSession(store);
            await session.RequestAsync("commit", operations, batchId);
            var saved = await session.RequestAsync("outcome", JsonLines.Json(new { batchId }));
            await Assert.That(saved.GetProperty("state").GetString()).IsEqualTo("saved");
            var current = store.Execute(local => local.Latest(schema.EntityId)!.Version);
            using var updated = await EntityApiTests.Submit(http, current with { VersionId = VersionId.New(), PreviousVersionId = current.VersionId });
            updated.EnsureSuccessStatusCode();
            var second = Guid.CreateVersion7().ToString();
            await session.RequestAsync("commit", JsonLines.Json(new { operations = new object[] {
                new { op = "draft", entityId = schema.EntityId.Value, category = schema.Category, content = VersionJson.Content(schema.Content) },
                new { op = "complete", entityId = schema.EntityId.Value, @as = "version" },
                new { op = "reference", entityId = schema.EntityId.Value, @as = "schema" },
                new { op = "draft", entityId = EntityId.New().Value, category = "input_set", content = new { members = new[] { current.VersionId.Value } }, bindings = new Dictionary<string,string> { ["/members/0"] = "schema" } } } }), second);
            await store.SendAsync();
            var rejected = await session.RequestAsync("outcome", JsonLines.Json(new { batchId = second }));
            await Assert.That(rejected.GetProperty("state").GetString()).IsEqualTo("rejected");
            await Assert.That(rejected.GetProperty("rule").GetString()).IsEqualTo("VERSION-6");
            await session.RequestAsync("repair", JsonLines.Json(new { rejectedVersionId = rejected.GetProperty("versionId").GetGuid(), action = "discard" }), Guid.NewGuid().ToString());
            var discarded = await session.RequestAsync("outcome", JsonLines.Json(new { batchId = second }));
            await Assert.That(discarded.GetProperty("state").GetString()).IsEqualTo("discarded");
        }
    }
}
