using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orisumi.Core;
using Orisumi.Testing;
using System.Net.Http.Json;

namespace Orisumi.Integration.Tests;

public sealed class SchemaDescriptionTests
{
    [Test]
    [Property("caseId", "api-schema-fields")]
    public async Task DescriptionsRoundTripAndChangesPreserveHistory()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var id = await writer.DeclareAsync(fields: Fields().ToJsonString());
        var first = await http.GetFromJsonAsync<JsonElement>($"/entities/{id.Value}");
        var fields = Fields(); fields["samples"]!["description"] = "New explanation";
        await writer.DeclareAsync(fields: fields.ToJsonString(), schemaId: id);
        using var original = await http.GetAsync($"/versions/{first.GetProperty("versionId").GetGuid()}");
        await Assert.That(JsonElement.DeepEquals((await WriteClient.JsonAsync(original)).GetProperty("content").GetProperty("fields"),
            JsonSerializer.SerializeToElement(Fields()))).IsTrue();
        using var current = await http.GetAsync($"/entities/{id.Value}");
        await Assert.That((await WriteClient.JsonAsync(current)).GetProperty("content").GetProperty("fields")
            .GetProperty("samples").GetProperty("description").GetString()).IsEqualTo("New explanation");
        // An old program binds its original Schema version without adding another version.
        await writer.DeclareAsync(fields: Fields().ToJsonString(), schemaId: id);
        using var chain = await http.GetAsync($"/entities/{id.Value}/versions");
        await Assert.That((await WriteClient.JsonAsync(chain)).GetProperty("versions").GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    [Arguments("field", "missing")]
    [Arguments("member", "empty")]
    [Arguments("item", "blank")]
    [Arguments("field", "number")]
    [Arguments("member", "null")]
    [Arguments("item", "object")]
    [Property("caseId", "api-schema-fields")]
    public async Task DescriptionErrorsPreserveStructuralAndPerVersionBoundaries(string location, string error)
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var fields = Fields();
        var field = (JsonObject)fields["samples"]!;
        var item = (JsonObject)field["items"]!;
        var target = location switch { "field" => field, "item" => item, _ => (JsonObject)item["fields"]!["value"]! };
        if (error == "missing") target.Remove("description");
        else target["description"] = error switch
        {
            "empty" => JsonValue.Create(""), "blank" => JsonValue.Create(" \t"), "number" => JsonValue.Create(3),
            "null" => null, _ => new JsonObject(),
        };
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var prefix = Guid.CreateVersion7();
        object Declaration(Guid id, string resource, object definitions) => new { schemaId = id, content = new { displayName = "Test", description = "Test type", resourceName = resource, kind = "observation", immutable = false, fields = definitions } };
        using var response = await http.PostAsJsonAsync("/writes", new { observer = writer.ObserverReference,
            schemas = new[] { Declaration(prefix, "prefix", new { }), Declaration(Guid.CreateVersion7(), "invalid", fields) }, writes = Array.Empty<object>() });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var problem = await WriteClient.JsonAsync(response);
        if (error is not ("number" or "null" or "object"))
            await Assert.That(problem.GetProperty("rule").GetString()).IsEqualTo("SCHEMA-10");
        using var read = await http.GetAsync($"/entities/{prefix}");
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test, Property("caseId", "api-schema-fields")]
    [Arguments("thing-fields", "SCHEMA-17")]
    [Arguments("thing-immutable", "SCHEMA-17")]
    [Arguments("kind-change", "SCHEMA-17")]
    [Arguments("reference-target", "SCHEMA-9")]
    [Arguments("id-field", "SCHEMA-7")]
    public async Task KindAndReferenceDeclarationsRespectSchemaRules(string change, string rule)
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var id = await writer.DeclareAsync("objects", kind: "thing");
        var fields = change switch
        {
            "thing-fields" => JsonNode.Parse(WriteApiTests.NameFields)!,
            "reference-target" => JsonNode.Parse("""{"targetId":{"type":"reference","description":"Target","required":true,"nullable":false}}""")!,
            "id-field" => JsonNode.Parse("""{"id":{"type":"string","description":"ID","required":true,"nullable":false}}""")!,
            _ => new JsonObject(),
        };
        using var response = await http.PostAsJsonAsync("writes", new { observer = writer.ObserverReference,
            schemas = new[] { new { schemaId = change == "kind-change" ? id.Value : Guid.CreateVersion7(),
                content = new { displayName = "Objects", description = "Objects", resourceName = change == "kind-change" ? "objects" : "invalid",
                    kind = change.StartsWith("thing", StringComparison.Ordinal) ? "thing" : "observation", immutable = change == "thing-immutable", fields } } },
            writes = Array.Empty<object>() });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await WriteClient.JsonAsync(response)).GetProperty("rule").GetString()).IsEqualTo(rule);
    }

    [Test, Property("caseId", "api-schema-fields")]
    public async Task BuiltInTypesNeedNoDeclarationAndAnObserversOwnDeclarationTakesPrecedence()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var name = new EntityId(Orisumi.Observer.Sdk.CommonSchemas.Name);
        var original = await writer.WriteAsync(name, new { thingId = writer.ObserverId.Value, name = "default" });
        var fields = """{"thingId":{"type":"reference","target":"thing","description":"Named thing","required":true,"nullable":false},"name":{"type":"string","description":"Name","required":true,"nullable":false},"extra":{"type":"string","description":"Extra","required":true,"nullable":false}}""";
        await writer.DeclareAsync("names", fields, name);
        var own = await writer.WriteAsync(name, new { thingId = writer.ObserverId.Value, name = "own", extra = "extra" });
        await Assert.That(own.SchemaVersionId == original.SchemaVersionId).IsFalse();
        var other = new WriteClient(http); await other.InitializeAsync();
        var fallback = await other.WriteAsync(name, new { thingId = writer.ObserverId.Value, name = "builtin" });
        await Assert.That(fallback.SchemaVersionId).IsEqualTo(original.SchemaVersionId);
    }

    private static JsonObject Fields() => JsonNode.Parse("""
        {"samples":{"type":"array","description":"Measured samples","required":true,"nullable":false,
          "items":{"type":"object","description":"One sample","nullable":false,"fields":{
            "value":{"type":"number","description":"Temperature in Celsius","required":true,"nullable":true}}}}}
        """)!.AsObject();
}
