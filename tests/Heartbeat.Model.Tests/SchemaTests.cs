using Heartbeat.Core;
using static Heartbeat.Model.Tests.ModelFixture;

namespace Heartbeat.Model.Tests;

public sealed class SchemaTests
{
    // ENTITY-3, VERSION-3, SCHEMA-1, SCHEMA-2, SCHEMA-3, SCHEMA-11, SCHEMA-12, SCHEMA-17
    [Test]
    public async Task TypeIsStableAndEachBusinessVersionUsesItsOwnSavedSchema()
    {
        using var f = new ModelFixture();
        var schema = await f.Schema();
        var old = await f.Business(schema);
        var nextSchema = await f.Save(Next(schema, new EntitySchema("Required title", "Title", "items",
            Json("""{"title":{"type":"string","required":true,"nullable":false}}"""))));
        await f.Business(schema);
        await Reject(() => f.Business(nextSchema), "SCHEMA-11");
        var updated = await f.Save(Next(old, new BusinessContent(Json("""{"title":"new"}"""))) with { SchemaVersionId = nextSchema.VersionId });
        var returned = await f.Save(Next(updated, old.Content) with { SchemaVersionId = schema.VersionId });
        await Assert.That(returned.SchemaVersionId).IsEqualTo(schema.VersionId);
        await Assert.That(await f.Service.ReadCurrentAsync(old.EntityId)).IsEqualTo(returned);
        await Reject(() => f.Save(Next(returned, updated.Content)), "SCHEMA-12");
        await Reject(() => f.Save(Next(returned) with { SchemaVersionId = nextSchema.VersionId }), "SCHEMA-11");
        await Assert.That((await f.Service.ReadVersionsAsync(old.EntityId)).Count).IsEqualTo(3);
        var savedUpdated = (await f.Service.ReadVersionAsync(updated.VersionId))!;
        await Assert.That(savedUpdated.SchemaVersionId).IsEqualTo(nextSchema.VersionId);
        await Assert.That(((BusinessContent)savedUpdated.Content).Fields.GetProperty("title").GetString()).IsEqualTo("new");
        var other = await f.Schema("others");
        await Reject(() => f.Save(Next(old) with { PreviousVersionId = returned.VersionId, SchemaVersionId = other.VersionId }), "ENTITY-3");
        await Reject(() => f.Save(Next(returned, new InputSet([schema.VersionId])) with { SchemaVersionId = null }), "ENTITY-3");
        await Reject(() => f.Save(New(new BusinessContent(Json("{}")))), "VERSION-3");
        await Reject(() => f.Save(New(new InputSet([schema.VersionId]), schema.VersionId)), "VERSION-3");
        await Reject(() => f.Save(New(new BusinessContent(Json("{}")), VersionId.New())), "SCHEMA-3");
        await Reject(() => f.Save(New(new BusinessContent(Json("{}")), old.VersionId)), "SCHEMA-3");
        await Assert.That(((BusinessContent)(await f.Service.ReadVersionAsync(old.VersionId))!.Content).Fields.GetRawText()).IsEqualTo("{}");
    }

    // SCHEMA-4: only nonempty names and descriptions are accepted; prose meaning is the writer's responsibility.
    [Test]
    [Arguments("", "Description")]
    [Arguments("Name", "")]
    [Arguments(" ", "Description")]
    [Arguments("Name", "\t")]
    [Arguments(null, "Description")]
    [Arguments("Name", null)]
    public async Task EmptyNamesAndDescriptionsReportSchemaMetadataViolation(string? displayName, string? description)
    {
        using var f = new ModelFixture();
        var invalid = New(new EntitySchema(displayName!, description!, "items", Json("{}")));
        await Reject(() => f.Save(invalid), "SCHEMA-4");
        await Assert.That(await f.Service.ReadVersionAsync(invalid.VersionId)).IsNull();
        var schema = await f.Schema();
        await Reject(() => f.Save(Next(schema, invalid.Content)), "SCHEMA-4");
        await Assert.That(await f.Service.ReadCurrentAsync(schema.EntityId)).IsEqualTo(schema);
        await f.Save(Next(schema, new EntitySchema("New name", "New description", "items", Json("{}"))));
    }

    // SCHEMA-5, SCHEMA-6
    [Test]
    public async Task ResourceNamesAreUniqueStableAndFollowTheDeclaredFormat()
    {
        using var f = new ModelFixture();
        var schema = await f.Schema();
        await Reject(() => f.Schema(), "SCHEMA-5");
        await Reject(() => f.Save(Next(schema, new EntitySchema("Items", "Items", "renamed", Json("{}")))), "SCHEMA-5");
        foreach (var invalid in new[] { "", "A", "1items", "-items", "my_items", "items\n" })
            await Reject(() => f.Schema(invalid), "SCHEMA-6");
        foreach (var valid in new[] { "a", "items-2", "schemas", "observers", "observations" }) await f.Schema(valid);
        await f.Save(Next(schema, new EntitySchema("Changed name", "Changed description", "items", Json("{}"))));
        await f.Service.DeleteAsync(schema.EntityId);
        await f.Schema();
    }

    // SCHEMA-7, SCHEMA-10, SCHEMA-12, SCHEMA-14
    [Test]
    [Arguments("{\"id\":{\"type\":\"string\",\"required\":true,\"nullable\":false}}", "SCHEMA-7")]
    [Arguments("{\"x\":{\"required\":true,\"nullable\":false}}", "SCHEMA-10")]
    [Arguments("{\"x\":{\"type\":\"string\",\"nullable\":false}}", "SCHEMA-10")]
    [Arguments("{\"x\":{\"type\":\"string\",\"required\":true}}", "SCHEMA-10")]
    [Arguments("{\"x\":{\"type\":\"string\",\"required\":true,\"nullable\":false,\"default\":\"x\"}}", "SCHEMA-12")]
    [Arguments("{\"x\":{\"type\":\"object\",\"required\":true,\"nullable\":false}}", "SCHEMA-14")]
    [Arguments("{\"x\":{\"type\":\"array\",\"required\":true,\"nullable\":false}}", "SCHEMA-14")]
    [Arguments("{\"x\":{\"type\":\"string\",\"required\":true,\"nullable\":false,\"fields\":{}}}", "SCHEMA-14")]
    [Arguments("{\"x\":{\"type\":\"array\",\"required\":true,\"nullable\":false,\"items\":{\"type\":\"string\",\"required\":false,\"nullable\":false}}}", "SCHEMA-10")]
    public async Task MalformedDeclarationsAreRejectedBeforeSaving(string fields, string rule)
    {
        using var f = new ModelFixture();
        await Reject(() => f.Schema(fields: fields), rule);
        await f.Schema(fields: """{"object":{"type":"object","required":true,"nullable":false,"fields":{"id":{"type":"string","required":false,"nullable":true}}}}""");
    }


    // SCHEMA-2, SCHEMA-11, SCHEMA-12, SCHEMA-13, SCHEMA-14
    [Test]
    public async Task OmittedAndNullRemainDistinctAndArrayConstraintsApplyAtEachLevel()
    {
        using var f = new ModelFixture();
        var schema = await f.Schema(fields: """
            {"optional":{"type":"string","required":false,"nullable":true},
             "list":{"type":"array","required":true,"nullable":true,"items":{"type":"object","nullable":false,
                 "fields":{"value":{"type":"integer","required":true,"nullable":true}}}}}
            """);
        var omitted = await f.Business(schema, """{"list":null}""");
        var withNull = await f.Business(schema, """{"optional":null,"list":[{"value":null},{"value":1e100}]}""");
        var read = (BusinessContent)(await f.Service.ReadVersionAsync(omitted.VersionId))!.Content;
        await Assert.That(read.Fields.TryGetProperty("optional", out _)).IsFalse();
        await Assert.That(((BusinessContent)withNull.Content).Fields.GetProperty("optional").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
        (string Content, string Rule)[] invalid =
        [
            ("{}", "SCHEMA-11"),
            ("{\"list\":[null]}", "SCHEMA-11"),
            ("{\"list\":[{}]}", "SCHEMA-11"),
            ("{\"list\":[{\"value\":1,\"extra\":true}]}", "SCHEMA-12"),
            ("{\"list\":[],\"extra\":true}", "SCHEMA-12"),
            ("{\"list\":[{\"value\":0.5}]}", "SCHEMA-2"),
            ("{\"list\":{}}", "SCHEMA-2"),
        ];
        foreach (var (content, rule) in invalid)
            await Reject(() => f.Business(schema, content), rule);
    }

    // VERSION-2, VERSION-7, SCHEMA-13
    [Test]
    public async Task JsonContentOwnsItsLifetimeAndRetriesCompareValues()
    {
        using var f = new ModelFixture();
        EntityVersion schema;
        using (var document = System.Text.Json.JsonDocument.Parse("""{"n":{"type":"number","required":true,"nullable":false},"s":{"type":"string","required":false,"nullable":true}}"""))
            schema = await f.Save(New(new EntitySchema("Item", "Item", "items", document.RootElement)));
        EntityVersion business;
        using (var document = System.Text.Json.JsonDocument.Parse("""{"n":1,"s":"x"}"""))
            business = await f.Save(New(new BusinessContent(document.RootElement), schema.VersionId));
        await Assert.That(await f.Save(business.Submission with { Content = new BusinessContent(Json("""{"s":"x","n":1.0}""")) })).IsEqualTo(business);
        await Reject(() => f.Save(business.Submission with { Content = new BusinessContent(Json("""{"n":1}""")) }), "VERSION-7");
        await Reject(() => f.Save(business.Submission with { Content = new BusinessContent(Json("""{"n":1,"s":null}""")) }), "VERSION-7");
    }
}
