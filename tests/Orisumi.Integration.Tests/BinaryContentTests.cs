using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orisumi.Application.Entities;
using Orisumi.Core;
using Orisumi.Infrastructure.Database;
using Orisumi.Observer.Sdk;
using static Orisumi.Integration.Tests.EntityApiTests;

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
    internal static VersionSubmission Item(VersionSubmission schema, BinaryContent content) =>
        Business(schema, JsonLines.Json(new { icon = content.Descriptor, nested = new { file = content.Descriptor }, files = new JsonElement?[] { content.Descriptor, null } }).GetRawText());
    internal static async Task<HttpResponseMessage> Upload(HttpClient http, string digest, byte[] data)
    {
        using var body = new ByteArrayContent(data);
        body.Headers.ContentType = new("application/octet-stream");
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
    public async Task ContentMustBeUploadedAndIsRetainedUntilEveryHistoricalUsageIsDeleted()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        byte[] bytes = [1, 2, 3, 4];
        var content = new BinaryContent(bytes, "image/png");
        var schema = Schema(fields: Fields);
        var first = Item(schema, content);
        using var missing = await Submit(http, schema, first);
        await CheckRule(missing, HttpStatusCode.Conflict, "BINARY-3");
        await Assert.That((await Json(missing)).GetProperty("index").GetInt32()).IsEqualTo(1);
        using var upload = await Upload(http, content.Digest, bytes);
        using var repeated = await Upload(http, content.Digest, bytes);
        await Assert.That(upload.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(repeated.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var staged = await http.GetAsync("/contents/" + content.Digest);
        await Assert.That(staged.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var saved = await Submit(http, schema, first);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var later = first with { VersionId = VersionId.New(), PreviousVersionId = first.VersionId };
        var noFile = later with { VersionId = VersionId.New(), PreviousVersionId = later.VersionId, Content = new BusinessContent(JsonLines.Json(new { icon = (object?)null })) };
        var other = Item(schema, content);
        using var more = await Submit(http, later, noFile, other);
        await Assert.That(more.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await Count(database.ConnectionString, "version_contents")).IsEqualTo(3);
        await Assert.That(await Count(database.ConnectionString, "contents")).IsEqualTo(1);
        using var read = await http.GetAsync("/contents/" + content.Digest);
        await Assert.That((await read.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes)).IsTrue();
        await Assert.That(read.Headers.CacheControl!.Private).IsTrue();
        await Assert.That(read.Headers.CacheControl.MaxAge).IsEqualTo(TimeSpan.FromDays(365));
        await Assert.That(read.Headers.CacheControl.Extensions.Any(value => value.Name == "immutable")).IsTrue();
        await Assert.That(read.Content.Headers.ContentType!.MediaType).IsEqualTo("application/octet-stream");
        await using var data = NpgsqlDataSource.Create(database.ConnectionString);
        await new PostgresEntityStore(data).RebuildProjectionsAsync();
        await Assert.That(await Count(database.ConnectionString, "version_contents")).IsEqualTo(3);
        using var deletedOther = await http.DeleteAsync("/entities/" + other.EntityId.Value);
        using var historical = await http.GetAsync("/contents/" + content.Digest);
        await Assert.That(historical.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var deletedFirst = await http.DeleteAsync("/entities/" + first.EntityId.Value);
        using var gone = await http.GetAsync("/contents/" + content.Digest);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await Count(database.ConnectionString, "contents")).IsEqualTo(0);
        await Assert.That(await Count(database.ConnectionString, "version_contents")).IsEqualTo(0);
    }

    [Test, Property("caseId", "api-binary-content")]
    public async Task InvalidDescriptorsUploadsAndSizesAreRejected()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        byte[] bytes = [42];
        var content = new BinaryContent(bytes, "image/png");
        var schema = Schema(fields: Fields);
        using var registered = await Submit(http, schema);
        using var mismatch = await Upload(http, content.Digest, [43]);
        await CheckRule(mismatch, HttpStatusCode.BadRequest, "BINARY-1");
        using var oversized = await Upload(http, content.Digest, new byte[8 * 1024 * 1024 + 1]);
        await Assert.That(oversized.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        var valid = content.Descriptor.GetRawText();
        string[] invalid = ["{}", valid[..^1] + ",\"extra\":1}", valid.Replace("sha256:", "SHA256:", StringComparison.Ordinal),
            valid.Replace(content.Digest, "sha256:abc", StringComparison.Ordinal), valid.Replace("\"size\":1", "\"size\":-1", StringComparison.Ordinal),
            valid.Replace("image/png", "image", StringComparison.Ordinal), valid.Replace("\"size\":1", "\"size\":1.5", StringComparison.Ordinal)];
        foreach (var descriptor in invalid)
        foreach (var value in new[] { "{\"icon\":" + descriptor + "}", "{\"icon\":null,\"nested\":{\"file\":" + descriptor + "}}", "{\"icon\":null,\"files\":[" + descriptor + "]}" })
        {
            using var rejected = await Submit(http, Business(schema, value));
            await CheckRule(rejected, HttpStatusCode.BadRequest, "BINARY-1");
        }
        using var uploaded = await Upload(http, content.Digest, bytes);
        using var wrongSize = await Submit(http, Business(schema, "{\"icon\":" + valid.Replace("\"size\":1", "\"size\":2", StringComparison.Ordinal) + "}"));
        await CheckRule(wrongSize, HttpStatusCode.BadRequest, "BINARY-3");
        await Assert.That(await Count(database.ConnectionString, "version_contents")).IsEqualTo(0);
    }

    [Test, Property("caseId", "api-binary-content")]
    public async Task RequiredAndNullableRemainIndependentForBinaryValues()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        foreach (var required in new[] { false, true })
        foreach (var nullable in new[] { false, true })
        {
            var schema = Schema("binary-" + required.ToString().ToLowerInvariant() + "-" + nullable.ToString().ToLowerInvariant(),
                JsonLines.Json(new { file = new { type = "binary", description = "File", required, nullable } }).GetRawText());
            using var saved = await Submit(http, schema);
            using var absent = await Submit(http, Business(schema));
            using var empty = await Submit(http, Business(schema, "{\"file\":null}"));
            await Assert.That(absent.StatusCode).IsEqualTo(required ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
            await Assert.That(empty.StatusCode).IsEqualTo(nullable ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
            if (required) await CheckRule(absent, HttpStatusCode.BadRequest, "SCHEMA-11");
            if (!nullable) await CheckRule(empty, HttpStatusCode.BadRequest, "SCHEMA-11");
        }
    }

    [Test, Property("caseId", "api-binary-content")]
    public async Task StagingExpiresButUploadsRefreshItAndUsedContentSurvives()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        var clock = new Clock();
        await using var factory = new OrisumiApiFactory(database.ConnectionString, configureServices: services => services.AddSingleton<TimeProvider>(clock));
        using var http = factory.CreateClient();
        var schema = Schema(fields: Fields);
        var first = new BinaryContent([1], "image/png");
        var second = new BinaryContent([2], "image/png");
        var third = new BinaryContent([3], "image/png");
        using var one = await Upload(http, first.Digest, [1]);
        using var two = await Upload(http, second.Digest, [2]);
        using var three = await Upload(http, third.Digest, [3]);
        using var saved = await Submit(http, schema, Item(schema, first));
        clock.Now = clock.Now.AddHours(23);
        using var refreshed = await Upload(http, third.Digest, [3]);
        clock.Now = clock.Now.AddHours(2);
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ContentService>().PruneAsync();
        using var expired = await Submit(http, Item(schema, second));
        await CheckRule(expired, HttpStatusCode.Conflict, "BINARY-3");
        using var retained = await http.GetAsync("/contents/" + first.Digest);
        await Assert.That(retained.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var fresh = await Submit(http, Item(schema, third));
        await Assert.That(fresh.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await Count(database.ConnectionString, "contents")).IsEqualTo(2);
    }

    private static async Task CheckRule(HttpResponseMessage response, HttpStatusCode status, string rule)
    {
        await Assert.That(response.StatusCode).IsEqualTo(status);
        await Assert.That((await Json(response)).GetProperty("rule").GetString()).IsEqualTo(rule);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
