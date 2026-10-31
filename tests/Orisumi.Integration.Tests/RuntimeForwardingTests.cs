using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Runtime.Forwarding;
using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

public sealed class RuntimeForwardingTests
{
    [Test, Property("caseId", "local-forwarding")]
    public async Task UnpairedAndOfflineWritesSurviveRestartAndLostResponsesDoNotDuplicate()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var backend = factory.CreateClient();
        var writer = new WriteClient(backend); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: WriteApiTests.NameFields);
        using var directory = new RuntimeHostingTests.RuntimeDirectory();
        using var network = new Network(factory.Server.CreateHandler()) { Offline = true };
        using var http = new HttpClient(network) { BaseAddress = new("http://localhost/") };
        var registered = false;
        var id = Guid.CreateVersion7();
        await using var server = new ForwardingServer(); await server.StartAsync(CancellationToken.None);
        using var local = new HttpClient { BaseAddress = new(server.InstanceAddress(id)) };
        await using (var queue = new WriteQueue(directory.Data, http, () => registered))
        {
            server.Add(id, queue);
            for (var i = 0; i < 2; i++)
            {
                using var accepted = await local.PostAsJsonAsync("writes", new { observer = writer.ObserverReference, writes = new[] { WriteClient.Write(schema, new { name = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }) } });
                await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            }
            await Assert.That(queue.Pending).IsEqualTo(2);
        }
        await using var restored = new WriteQueue(directory.Data, http, () => registered);
        server.Add(id, restored);
        await Assert.That(restored.Pending).IsEqualTo(2);
        registered = true; network.Offline = false; network.DropNextResponse = true;
        await Until(() => restored.Pending == 0);
        using var read = await backend.GetAsync("/entities?resourceName=items");
        var versions = (await WriteClient.JsonAsync(read)).GetProperty("versions");
        await Assert.That(versions.GetArrayLength()).IsEqualTo(2);
        await Assert.That(versions.EnumerateArray().Select(v => v.GetProperty("content").GetProperty("name").GetString()).SequenceEqual(["0", "1"])).IsTrue();
        await Assert.That(restored.Rejected).IsEqualTo(0);
        await Assert.That(network.Sequences.Take(2).SequenceEqual([1L, 1L])).IsTrue();
    }

    [Test, Property("caseId", "local-forwarding")]
    public async Task RejectionsAreRetainedAndResentAfterSchemaCorrectionWhileOtherWritesContinue()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var backend = factory.CreateClient();
        var writer = new WriteClient(backend); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: WriteApiTests.NameFields);
        using var directory = new RuntimeHostingTests.RuntimeDirectory();
        await using var queue = new WriteQueue(directory.Data, backend, () => true);
        await queue.EnqueueAsync(JsonSerializer.SerializeToUtf8Bytes(new { observer = writer.ObserverReference, writes = new[] {
            WriteClient.Write(schema, new { }), WriteClient.Write(schema, new { name = "after" }) } }), null, CancellationToken.None);
        await Until(() => queue.Pending == 0);
        await Assert.That(queue.Rejected).IsEqualTo(1);
        await Assert.That(queue.LastRejection).IsNotNull();
        await writer.DeclareAsync(fields: WriteApiTests.NameFields.Replace("\"required\":true", "\"required\":false", StringComparison.Ordinal), schemaId: schema);
        await Assert.That(await queue.ResendRejectedAsync(CancellationToken.None)).IsEqualTo(1);
        await Until(() => queue.Pending == 0);
        await Assert.That(queue.Rejected).IsEqualTo(0);
        using var read = await backend.GetAsync("/entities?resourceName=items");
        await Assert.That((await WriteClient.JsonAsync(read)).GetProperty("versions").GetArrayLength()).IsEqualTo(2);
    }

    [Test, Property("caseId", "local-forwarding")]
    public async Task FilesArriveBeforeWritesAndOversizedRequestsAreNotPersisted()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var backend = factory.CreateClient();
        var writer = new WriteClient(backend); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: BinaryContentTests.Fields);
        using var directory = new RuntimeHostingTests.RuntimeDirectory();
        await using var queue = new WriteQueue(directory.Data, backend, () => true);
        await using var server = new ForwardingServer(); await server.StartAsync(CancellationToken.None);
        var id = Guid.CreateVersion7(); server.Add(id, queue);
        using var local = new HttpClient { BaseAddress = new(server.InstanceAddress(id)) };
        byte[] bytes = [1, 2, 3]; var digest = BinaryValue.ComputeDigest(bytes);
        using var file = new ByteArrayContent(bytes); using var uploaded = await local.PutAsync("contents/" + digest, file);
        await Assert.That(uploaded.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var accepted = await local.PostAsJsonAsync("writes", new { observer = writer.ObserverReference, writes = new[] { WriteClient.Write(schema, new { icon = new { digest, mediaType = "image/png", size = 3 } }) } });
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Until(() => queue.Pending == 0);
        await Assert.That(queue.Rejected).IsEqualTo(0);
        using var content = await backend.GetAsync("contents/" + digest); content.EnsureSuccessStatusCode();
        await Assert.That((await content.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes)).IsTrue();
        local.DefaultRequestHeaders.ExpectContinue = true;
        using var oversizedPost = new ByteArrayContent(new byte[WriteQueue.PostLimit + 1]);
        using var post = await local.PostAsync("writes", oversizedPost);
        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        using var oversizedPut = new ByteArrayContent(new byte[WriteQueue.PutLimit + 1]);
        using var put = await local.PutAsync("contents/" + digest, oversizedPut);
        await Assert.That(put.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(queue.Pending).IsEqualTo(0);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
    private sealed class Network(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal bool Offline { get; set; }
        internal bool DropNextResponse { get; set; }
        internal List<long> Sequences { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Offline) throw new HttpRequestException("Offline");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Sequences.Add(body.RootElement.GetProperty("sequence").GetInt64());
            var response = await base.SendAsync(request, token);
            if (DropNextResponse) { DropNextResponse = false; response.Dispose(); throw new HttpRequestException("Response lost"); }
            return response;
        }
    }
}
