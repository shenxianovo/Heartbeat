using System.Net;
using System.Text.Json;
using Orisumi.Observer.Sdk;
using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

public sealed class SdkTests
{
    [Test, Property("caseId", "sdk-languages")]
    public async Task FixtureWritesAndReadsThroughHttpAndScriptReimportIsUnchanged()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var schema = Observer.Fixture.Program.SchemaId;
        var key = new RecognitionKey(null, "fixture", "script");
        await using var data = new DataClient(http, http, Ref.Recognize(CommonSchemas.Program, key));
        var read = await Observer.Fixture.Program.WriteAndReadAsync(data);
        await Assert.That(read.GetProperty("content").GetProperty("name").GetString()).IsEqualTo("sample");
        var item = new WriteItem(schema, new { name = "row" }, "direct", new(null, "row", "1"), Time: FactTime.Unknown());
        var first = (await data.WriteAsync(item))!;
        var second = (await data.WriteAsync(item))!;
        await Assert.That(second.Changed).IsFalse();
        await Assert.That(second.Version!.Value.GetProperty("versionId").GetGuid()).IsEqualTo(first.Version!.Value.GetProperty("versionId").GetGuid());
        var list = await data.ListAsync("entities?resourceName=fixture-observers&limit=1");
        await Assert.That(list.Count).IsEqualTo(3);
        var versions = await data.VersionsAsync([first.Version!.Value.GetProperty("versionId").GetGuid()]);
        await Assert.That(versions.GetProperty("results")[0].GetProperty("status").GetString()).IsEqualTo("found");
    }

    [Test, Property("caseId", "sdk-languages")]
    public async Task NestedFilesUploadBeforeOrderedBatchesAndReadPreservesJson()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("http://sdk/") };
        await using var data = new DataClient(http, http, Ref.Entity(Guid.CreateVersion7()));
        var schema = Guid.CreateVersion7();
        var key = new RecognitionKey(null, "native", "a");
        var file = new BinaryContent([1, 2, 3], "image/png");
        var items = new[] {
            new WriteItem(schema, new { name = new string('a', 6 * 1024 * 1024), nested = new { files = new[] {file}, target = Ref.Recognize(schema, key), optional = (string?)null } }, "direct", Time: FactTime.Unknown()),
            new WriteItem(schema, new { name = new string('b', 6 * 1024 * 1024), target = Ref.Entity(Guid.CreateVersion7()) }, "direct"),
            new WriteItem(schema, new { name = "last", target = Ref.Version(Guid.CreateVersion7()) }, "direct") };
        await data.WriteAllAsync(items);
        await Assert.That(handler.Methods.ToArray()).IsEquivalentTo(["PUT", "POST", "POST"]);
        await Assert.That(handler.Posts.Count).IsEqualTo(2);
        var first = handler.Posts[0].GetProperty("writes")[0];
        var nested = first.GetProperty("content").GetProperty("nested");
        await Assert.That(nested.GetProperty("files")[0].GetProperty("digest").GetString()).IsEqualTo(file.Digest);
        await Assert.That(nested.GetProperty("optional").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(nested.GetProperty("target").GetProperty("key").GetProperty("scope").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(first.GetProperty("time").GetProperty("startAt").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(handler.Posts[1].GetProperty("writes")[1].GetProperty("content").GetProperty("name").GetString()).IsEqualTo("last");
        var raw = await data.VersionAsync(Guid.CreateVersion7());
        await Assert.That(raw.GetProperty("content").GetProperty("unknown").GetRawText()).IsEqualTo("123456789012345678901234567890");
        await Assert.That(raw.GetProperty("content").TryGetProperty("absent", out _)).IsFalse();
        data.WatchStopping(new StringReader(""));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!data.Stopping.IsCancellationRequested) await Task.Delay(10, timeout.Token);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];
        public List<JsonElement> Posts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Methods.Add(request.Method.Method);
            if (request.Method == HttpMethod.Post)
            {
                var bytes = await request.Content!.ReadAsByteArrayAsync(token);
                if (bytes.Length > 10 * 1024 * 1024) throw new InvalidDataException("Oversized batch");
                Posts.Add(JsonSerializer.Deserialize<JsonElement>(bytes));
                return new(HttpStatusCode.Accepted);
            }
            return request.Method == HttpMethod.Put ? new(HttpStatusCode.NoContent) : new(HttpStatusCode.OK)
            { Content = new StringContent("""{"schemaVersionId":"019e1bbb-ec00-7000-8000-000000000001","content":{"unknown":123456789012345678901234567890,"null":null}}""") };
        }
    }
}
