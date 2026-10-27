using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orisumi.Core;

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
        var first = EntityApiTests.Schema(fields: Fields().ToJsonString());
        using var saved = await EntityApiTests.Submit(http, first);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var fields = Fields();
        fields["samples"]!["description"] = "New explanation";
        var next = first with { VersionId = VersionId.New(), PreviousVersionId = first.VersionId,
            Content = new EntitySchema("Items", "Items", "items", JsonSerializer.SerializeToElement(fields)) };
        using var changed = await EntityApiTests.Submit(http, next);
        changed.EnsureSuccessStatusCode();
        using var original = await http.GetAsync($"/versions/{first.VersionId.Value}");
        await Assert.That(JsonElement.DeepEquals((await EntityApiTests.Json(original)).GetProperty("content").GetProperty("fields"),
            ((EntitySchema)first.Content).Fields)).IsTrue();
        using var current = await http.GetAsync($"/entities/{first.EntityId.Value}");
        await Assert.That((await EntityApiTests.Json(current)).GetProperty("content").GetProperty("fields")
            .GetProperty("samples").GetProperty("description").GetString()).IsEqualTo("New explanation");
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
        var prefix = EntityApiTests.Schema("prefix");
        var invalid = EntityApiTests.Schema("invalid", fields.ToJsonString());
        using var response = await EntityApiTests.Submit(http, prefix, invalid);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var problem = await EntityApiTests.Json(response);
        var structural = error is "number" or "null" or "object";
        await Assert.That(problem.TryGetProperty("index", out _)).IsEqualTo(!structural);
        if (!structural)
        {
            await Assert.That(problem.GetProperty("index").GetInt32()).IsEqualTo(1);
            await Assert.That(problem.GetProperty("rule").GetString()).IsEqualTo("SCHEMA-10");
        }
        using var read = await http.GetAsync($"/versions/{prefix.VersionId.Value}");
        await Assert.That(read.StatusCode).IsEqualTo(structural ? HttpStatusCode.NotFound : HttpStatusCode.OK);
    }

    private static JsonObject Fields() => JsonNode.Parse("""
        {"samples":{"type":"array","description":"Measured samples","required":true,"nullable":false,
          "items":{"type":"object","description":"One sample","nullable":false,"fields":{
            "value":{"type":"number","description":"Temperature in Celsius","required":true,"nullable":true}}}}}
        """)!.AsObject();
}
