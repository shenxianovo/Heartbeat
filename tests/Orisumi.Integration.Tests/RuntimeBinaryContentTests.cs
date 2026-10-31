using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Runtime;
using Orisumi.Observer.Sdk;

namespace Orisumi.Integration.Tests;

public sealed class RuntimeBinaryContentTests
{
    internal static JsonElement Batch(VersionId schema, JsonElement descriptor, JsonElement[] contents) =>
        JsonLines.Json(new { operations = new[] { new { op = "write", entityId = EntityId.New().Value, category = "business",
            schemaVersionId = schema.Value, previousVersionId = (Guid?)null, content = new { icon = descriptor } } }, contents });

    [Test, Property("caseId", "runtime-binary-content")]
    public async Task BatchValidatesBytesBeforeAnythingIsSent()
    {
        using var backend = new MemoryBackend();
        using var http = backend.CreateClient();
        var session = new RuntimeObserverSession(http, EntityId.New());
        var schema = await RegisterSchemaAsync(session);
        var content = new BinaryContent([1, 2, 3], "image/png");
        var posts = backend.Posts.Count;
        await Assert.That(async () => await session.RequestAsync("commit", Batch(schema, content.Descriptor, []))).Throws<ArgumentException>();
        var badHash = Batch(schema, content.Descriptor, [JsonLines.Json(new { digest = content.Digest, data = new byte[] { 4 } })]);
        await Assert.That(async () => await session.RequestAsync("commit", badHash)).Throws<ArgumentException>();
        var badSize = Batch(schema, JsonLines.Json(new { digest = content.Digest, mediaType = "image/png", size = 8 }), [content.Payload]);
        await Assert.That(async () => await session.RequestAsync("commit", badSize)).Throws<ArgumentException>();
        await Assert.That(backend.Posts.Count).IsEqualTo(posts);
        await Assert.That(backend.Contents.Count).IsEqualTo(0);
    }

    [Test, Property("caseId", "runtime-binary-content")]
    public async Task BytesAreUploadedBeforeTheVersionsThatUseThem()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var session = new RuntimeObserverSession(http, EntityId.New());
        var schema = await RegisterSchemaAsync(session);
        var content = new BinaryContent([7, 8], "image/png");
        await session.RequestAsync("commit", Batch(schema, content.Descriptor, [content.Payload]));
        await session.RequestAsync("commit", Batch(schema, content.Descriptor, [content.Payload]));
        using var read = await http.GetAsync("contents/" + content.Digest);
        await Assert.That((await read.Content.ReadAsByteArrayAsync()).SequenceEqual(new byte[] { 7, 8 })).IsTrue();
        await Assert.That(await BinaryContentTests.Count(database.ConnectionString, "contents")).IsEqualTo(1);
    }

    [Test, Property("caseId", "runtime-binary-content")]
    public async Task ClientReadsContentFromTheBackend()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var session = new RuntimeObserverSession(http, EntityId.New());
        var schema = await RegisterSchemaAsync(session);
        var content = new BinaryContent([9], "image/png");
        await session.RequestAsync("commit", Batch(schema, content.Descriptor, [content.Payload]));
        var query = JsonLines.Json(new { digest = content.Digest });
        var online = new RuntimeReader(http);
        await Assert.That((await online.ContentAsync(query, CancellationToken.None)).GetProperty("data").GetBytesFromBase64().Single()).IsEqualTo((byte)9);
        var missing = JsonLines.Json(new { digest = new BinaryContent([10], "image/png").Digest });
        await Assert.That(async () => await online.ContentAsync(missing, CancellationToken.None)).Throws<KeyNotFoundException>();
        await Assert.That(async () => await new RuntimeReader(http, () => false).ContentAsync(query, CancellationToken.None)).Throws<HttpRequestException>();
    }

    private static async Task<VersionId> RegisterSchemaAsync(RuntimeObserverSession session)
    {
        var schema = EntityApiTests.Schema(fields: BinaryContentTests.Fields);
        var result = await session.RequestAsync("registerSchema", JsonLines.Json(new { entityId = schema.EntityId.Value, content = VersionJson.Content(schema.Content) }));
        return new(result.GetProperty("versionId").GetGuid());
    }
}
