using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;
using Orisumi.Core;
using Orisumi.Observer.Runtime;
using Orisumi.Observer.Runtime.Store;
using Orisumi.Observer.Sdk;

namespace Orisumi.Integration.Tests;

public sealed class RuntimeBinaryContentTests
{
    internal static JsonElement Batch(VersionId schema, JsonElement descriptor, JsonElement[] contents, string strategy = "Immediate") =>
        JsonLines.Json(new { operations = new[] { new { op = "draft", entityId = EntityId.New().Value, category = "business",
            schemaVersionId = schema.Value, content = new { icon = descriptor }, strategy } }, contents });

    [Test, Property("caseId", "runtime-binary-content")]
    public async Task BatchValidatesBytesAtomicallyAndDeduplicatesAcrossBatches()
    {
        using var directory = new RuntimeHostingTests.RuntimeDirectory();
        using var http = new HttpClient(new RuntimeProtocolTests.OfflineHandler()) { BaseAddress = new("http://localhost/") };
        var file = Path.Combine(directory.Data, "test.sqlite");
        using var store = new ObserverStore(file, EntityId.New(), http);
        var schema = store.RegisterSchemaLocally(EntityId.New(), (EntitySchema)EntityApiTests.Schema(fields: BinaryContentTests.Fields).Content);
        var session = new LocalObserverSession(store);
        var content = new BinaryContent([1, 2, 3], "image/png");
        var before = store.Execute(local => local.Versions().Count);
        var missing = Batch(schema, content.Descriptor, []);
        await Assert.That(async () => await session.RequestAsync("commit", missing, "retry")).Throws<ArgumentException>();
        var badHash = Batch(schema, content.Descriptor, [JsonLines.Json(new { digest = content.Digest, data = new byte[] { 4 } })]);
        await Assert.That(async () => await session.RequestAsync("commit", badHash, "retry")).Throws<ArgumentException>();
        var badSize = Batch(schema, JsonLines.Json(new { digest = content.Digest, mediaType = "image/png", size = 8 }), [content.Payload]);
        await Assert.That(async () => await session.RequestAsync("commit", badSize, "retry")).Throws<ArgumentException>();
        await Assert.That(store.Execute(local => local.Versions().Count)).IsEqualTo(before);
        await Assert.That(store.Execute(local => local.ReadContent(content.Digest))).IsNull();
        var good = Batch(schema, content.Descriptor, [content.Payload]);
        await session.RequestAsync("commit", good, "retry");
        await session.RequestAsync("commit", good, "retry");
        await session.RequestAsync("commit", Batch(schema, content.Descriptor, [content.Payload]), "second");
        using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM local_contents";
        await Assert.That((long)command.ExecuteScalar()!).IsEqualTo(1);
        await Assert.That(store.Execute(local => local.ReadContent(content.Digest))!.SequenceEqual(new byte[] { 1, 2, 3 })).IsTrue();
        // Existing local bytes do not excuse a missing payload in a later batch.
        await Assert.That(async () => await session.RequestAsync("commit", missing, "third")).Throws<ArgumentException>();
    }

    [Test, Property("caseId", "runtime-binary-content")]
    public async Task OfflineRestartAndExpiredStagingReuploadBeforeSavingAndReleaseLocalBytes()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var gate = new ExpiringBackend(factory.Server.CreateHandler(), database.ConnectionString) { Offline = true };
        using var http = new HttpClient(gate) { BaseAddress = new("http://localhost/") };
        using var directory = new RuntimeHostingTests.RuntimeDirectory();
        var file = Path.Combine(directory.Data, "test.sqlite");
        var owner = EntityId.New();
        var content = new BinaryContent([7, 8], "image/png");
        using (var store = new ObserverStore(file, owner, http))
        {
            var schema = store.RegisterSchemaLocally(EntityId.New(), (EntitySchema)EntityApiTests.Schema(fields: BinaryContentTests.Fields).Content);
            await new LocalObserverSession(store).RequestAsync("commit", Batch(schema, content.Descriptor, [content.Payload]), "one");
            await new LocalObserverSession(store).RequestAsync("commit", Batch(schema, content.Descriptor, [content.Payload]), "two");
            await store.SendAsync();
            await Assert.That(store.Status.Pending + store.Status.Drafts).IsGreaterThan(0);
        }
        gate.Offline = false;
        using var restarted = new ObserverStore(file, owner, http);
        await restarted.SendAsync();
        await Assert.That(restarted.Status.Rejection).IsNull();
        await Assert.That(restarted.Status.Error).IsNull();
        await Assert.That(restarted.Status.Pending + restarted.Status.Drafts).IsEqualTo(0);
        await Assert.That(gate.Uploads).IsEqualTo(2);
        await Assert.That(gate.Expired).IsTrue();
        await Assert.That(restarted.Execute(local => local.ReadContent(content.Digest))).IsNull();
        using var read = await http.GetAsync("contents/" + content.Digest);
        await Assert.That((await read.Content.ReadAsByteArrayAsync()).SequenceEqual(new byte[] { 7, 8 })).IsTrue();
        await Assert.That(await BinaryContentTests.Count(database.ConnectionString, "contents")).IsEqualTo(1);
    }

    [Test, Property("caseId", "runtime-binary-content")]
    public async Task ClientReadsLocalContentWithoutAnInstanceAndUsesBackendAfterConfirmation()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        using var directory = new RuntimeHostingTests.RuntimeDirectory();
        using var packages = new PackageStore(directory.Data, null, null, http);
        var owner = EntityId.New();
        await using var observer = new ManagedObserver(packages, new("example", "Example", "1.0", owner.Value, false, false, JsonLines.Json(new { })), http);
        var schema = observer.Store.RegisterSchemaLocally(EntityId.New(), (EntitySchema)EntityApiTests.Schema(fields: BinaryContentTests.Fields).Content);
        var content = new BinaryContent([9], "image/png");
        await new LocalObserverSession(observer.Store).RequestAsync("commit", Batch(schema, content.Descriptor, [content.Payload]), "one");
        var query = JsonLines.Json(new { digest = content.Digest });
        var reader = new RuntimeReader(http, () => false);
        await Assert.That((await reader.ContentAsync([observer], query, CancellationToken.None)).GetProperty("data").GetBytesFromBase64().Single()).IsEqualTo((byte)9);
        await observer.Store.SendAsync();
        var online = new RuntimeReader(http);
        await Assert.That((await online.ContentAsync([observer], query, CancellationToken.None)).GetProperty("data").GetBytesFromBase64().Single()).IsEqualTo((byte)9);
        var missing = JsonLines.Json(new { digest = new BinaryContent([10], "image/png").Digest });
        await Assert.That(async () => await online.ContentAsync([observer], missing, CancellationToken.None)).Throws<KeyNotFoundException>();
        await Assert.That(async () => await reader.ContentAsync([observer], query, CancellationToken.None)).Throws<HttpRequestException>();
    }

    [Test, Property("caseId", "runtime-binary-content")]
    public async Task UploadedDigestsSurviveByteRemovalAndRestartAndRecoverAfterBackendDeletion()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var gate = new ExpiringBackend(factory.Server.CreateHandler(), database.ConnectionString) { ExpireBeforeSubmit = false };
        using var http = new HttpClient(gate) { BaseAddress = new("http://localhost/") };
        using var directory = new RuntimeHostingTests.RuntimeDirectory();
        var file = Path.Combine(directory.Data, "test.sqlite");
        var owner = EntityId.New();
        var content = new BinaryContent([4, 5], "image/png");
        VersionId schema;
        using (var store = new ObserverStore(file, owner, http))
        {
            schema = store.RegisterSchemaLocally(EntityId.New(), (EntitySchema)EntityApiTests.Schema(fields: BinaryContentTests.Fields).Content);
            await new LocalObserverSession(store).RequestAsync("commit", Batch(schema, content.Descriptor, [content.Payload]), "one");
            await store.SendAsync();
            await Assert.That(store.Execute(local => local.ReadContent(content.Digest))).IsNull();
        }
        await Assert.That(gate.Uploads).IsEqualTo(1);
        using var restarted = new ObserverStore(file, owner, http);
        await new LocalObserverSession(restarted).RequestAsync("commit", Batch(schema, content.Descriptor, [content.Payload]), "two");
        await restarted.SendAsync();
        await Assert.That(restarted.Status.Pending + restarted.Status.Drafts).IsEqualTo(0);
        await Assert.That(gate.Uploads).IsEqualTo(1);
        await Assert.That(restarted.Execute(local => local.ReadContent(content.Digest))).IsNull();
        foreach (var entity in restarted.Execute(local => local.Versions().Where(version => version.Version.Content is BusinessContent).Select(version => version.Version.EntityId).ToArray()))
        {
            using var deleted = await http.DeleteAsync("entities/" + entity.Value);
            deleted.EnsureSuccessStatusCode();
        }
        await new LocalObserverSession(restarted).RequestAsync("commit", Batch(schema, content.Descriptor, [content.Payload]), "three");
        await restarted.SendAsync();
        await Assert.That(gate.Uploads).IsEqualTo(2);
        await Assert.That(restarted.Status.Rejection).IsNull();
        await Assert.That(restarted.Status.Pending + restarted.Status.Drafts).IsEqualTo(0);
        await Assert.That(restarted.Execute(local => local.ReadContent(content.Digest))).IsNull();
    }

    private sealed class ExpiringBackend(HttpMessageHandler inner, string connectionString) : DelegatingHandler(inner)
    {
        public bool Offline { get; set; }
        public int Uploads { get; private set; }
        public bool Expired { get; private set; }
        public bool ExpireBeforeSubmit { get; set; } = true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline) throw new HttpRequestException("Offline");
            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath.StartsWith("/contents/", StringComparison.Ordinal)) Uploads++;
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/versions" && Uploads > 0 && !Expired && ExpireBeforeSubmit)
            {
                await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand("DELETE FROM contents WHERE digest NOT IN (SELECT digest FROM version_contents)", connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
                Expired = true;
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
