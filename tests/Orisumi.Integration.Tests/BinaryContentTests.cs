using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orisumi.Application.Entities;
using Orisumi.Core;
using Orisumi.Infrastructure.Database;
using Orisumi.Observer.Sdk;
using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

public sealed class BinaryContentTests
{
    internal const string Fields = """
        {"icon":{"type":"binary","description":"Image","required":true,"nullable":true},
         "nested":{"type":"object","description":"Nested","required":false,"nullable":false,"fields":{
           "file":{"type":"binary","description":"File","required":true,"nullable":false}}},
         "files":{"type":"array","description":"Files","required":false,"nullable":false,"items":{
           "type":"binary","description":"File","nullable":true}}}
        """;
    internal static async Task<HttpResponseMessage> Upload(HttpClient http, string digest, byte[] data)
    {
        using var body = new ByteArrayContent(data); body.Headers.ContentType = new("application/octet-stream");
        return await http.PutAsync("/contents/" + digest, body);
    }
    internal static async Task<long> Count(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM " + table, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Test, Property("caseId", "api-binary-content")]
    public async Task ContentRequiresUploadAndAllHistoricalUsagesControlRetention()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: Fields);
        byte[] bytes = [1, 2, 3, 4]; var content = new BinaryContent(bytes, "image/png");
        var value = new { icon = content.Descriptor, nested = new { file = content.Descriptor }, files = new JsonElement?[] { content.Descriptor, null } };
        var missing = await writer.SendAsync([WriteClient.Write(schema, value)]);
        await Error(missing, 409, "BINARY-3");
        using var upload = await Upload(http, content.Digest, bytes); using var repeated = await Upload(http, content.Digest, bytes);
        await Assert.That(upload.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(repeated.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var staged = await http.GetAsync("/contents/" + content.Digest);
        await Assert.That(staged.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var first = await writer.WriteAsync(schema, value, "first");
        await writer.WriteAsync(schema, new { icon = (object?)null }, "first");
        var other = await writer.WriteAsync(schema, value);
        await Assert.That(await Count(database.ConnectionString, "version_contents")).IsEqualTo(2);
        using var read = await http.GetAsync("/contents/" + content.Digest);
        await Assert.That((await read.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes)).IsTrue();
        await Assert.That(read.Headers.CacheControl!.Private).IsTrue();
        await Assert.That(read.Headers.CacheControl.MaxAge).IsEqualTo(TimeSpan.FromDays(365));
        await Assert.That(read.Headers.CacheControl.Extensions.Any(v => v.Name == "immutable")).IsTrue();
        await Assert.That(read.Content.Headers.ContentType!.MediaType).IsEqualTo("application/octet-stream");
        await using var data = NpgsqlDataSource.Create(database.ConnectionString);
        await new PostgresEntityStore(data).RebuildProjectionsAsync();
        await Assert.That(await Count(database.ConnectionString, "version_contents")).IsEqualTo(2);
        using var deletedOther = await http.DeleteAsync("/entities/" + other.EntityId.Value);
        using var historical = await http.GetAsync("/contents/" + content.Digest);
        await Assert.That(historical.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var deletedFirst = await http.DeleteAsync("/entities/" + first.EntityId.Value);
        using var gone = await http.GetAsync("/contents/" + content.Digest);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await Count(database.ConnectionString, "contents")).IsEqualTo(0);
    }

    [Test, Property("caseId", "api-binary-content")]
    public async Task InvalidDescriptorsUploadsAndSizesAreRejected()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: Fields);
        var content = new BinaryContent([42], "image/png");
        using var mismatch = await Upload(http, content.Digest, [43]);
        await Assert.That(mismatch.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var oversized = await Upload(http, content.Digest, new byte[8 * 1024 * 1024 + 1]);
        await Assert.That(oversized.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        var valid = content.Descriptor.GetRawText();
        string[] invalid = ["{}", valid[..^1] + ",\"extra\":1}", valid.Replace("sha256:", "SHA256:", StringComparison.Ordinal),
            valid.Replace(content.Digest, "sha256:abc", StringComparison.Ordinal), valid.Replace("\"size\":1", "\"size\":-1", StringComparison.Ordinal),
            valid.Replace("image/png", "image", StringComparison.Ordinal), valid.Replace("\"size\":1", "\"size\":1.5", StringComparison.Ordinal)];
        foreach (var descriptor in invalid)
        foreach (var value in new[] { "{\"icon\":" + descriptor + "}", "{\"icon\":null,\"nested\":{\"file\":" + descriptor + "}}", "{\"icon\":null,\"files\":[" + descriptor + "]}" })
            await Error(await writer.SendAsync([WriteClient.Write(schema, JsonSerializer.Deserialize<JsonElement>(value))]), 400, "BINARY-1");
        using var uploaded = await Upload(http, content.Digest, [42]);
        await Error(await writer.SendAsync([WriteClient.Write(schema, JsonSerializer.Deserialize<JsonElement>("{\"icon\":" + valid.Replace("\"size\":1", "\"size\":2", StringComparison.Ordinal) + "}"))]), 400, "BINARY-3");
    }

    [Test, Property("caseId", "api-binary-content")]
    public async Task StagingExpiresButUploadsRefreshItAndUsedContentSurvives()
    {
        await using var database = await DatabaseFixture.CreateAsync(); var clock = new Clock();
        await using var factory = new OrisumiApiFactory(database.ConnectionString, configureServices: s => s.AddSingleton<TimeProvider>(clock));
        using var http = factory.CreateClient(); var writer = new WriteClient(http); await writer.InitializeAsync();
        var schema = await writer.DeclareAsync(fields: Fields);
        var first = new BinaryContent([1], "image/png"); var second = new BinaryContent([2], "image/png"); var third = new BinaryContent([3], "image/png");
        using var one = await Upload(http, first.Digest, [1]); using var two = await Upload(http, second.Digest, [2]); using var three = await Upload(http, third.Digest, [3]);
        await writer.WriteAsync(schema, new { icon = first.Descriptor });
        clock.Now += TimeSpan.FromHours(23); using var refreshed = await Upload(http, third.Digest, [3]);
        clock.Now += TimeSpan.FromHours(2); using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ContentService>().PruneAsync();
        await Error(await writer.SendAsync([WriteClient.Write(schema, new { icon = second.Descriptor })]), 409, "BINARY-3");
        using var retained = await http.GetAsync("/contents/" + first.Digest); retained.EnsureSuccessStatusCode();
        await writer.WriteAsync(schema, new { icon = third.Descriptor });
        await Assert.That(await Count(database.ConnectionString, "contents")).IsEqualTo(2);
    }
    private static async Task Error(JsonElement response, int status, string rule)
    {
        var error = response.GetProperty("results")[0].GetProperty("error");
        await Assert.That(error.GetProperty("status").GetInt32()).IsEqualTo(status);
        await Assert.That(error.GetProperty("rule").GetString()).IsEqualTo(rule);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
