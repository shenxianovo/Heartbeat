using Orisumi.Core;
using static Orisumi.Model.Tests.ModelFixture;

namespace Orisumi.Model.Tests;

public sealed class EntityReferenceTests
{
    // ENTITY-5, SCHEMA-9, SCHEMA-14, VERSION-12
    [Test]
    public async Task NestedReferencesKeepEveryPositionAndIgnoreOrdinaryStrings()
    {
        using var f = new ModelFixture();
        var target = await f.Schema("targets");
        var schema = await f.Schema("nested", """
            {"direct":{"type":"reference","target":"version","description":"Test field value.","required":true,"nullable":false},
             "a/b~c":{"type":"array","description":"Test field value.","required":true,"nullable":false,"items":{"type":"reference","target":"version","description":"Test field value.","nullable":true}},
             "objects":{"type":"array","description":"Test field value.","required":true,"nullable":false,"items":{"type":"object","description":"Test field value.","nullable":false,"fields":{
                "ref":{"type":"reference","target":"version","description":"Test field value.","required":true,"nullable":false},
                "text":{"type":"string","description":"Test field value.","required":true,"nullable":false}}}}}
            """);
        var reading = await f.Observe(schema, $$"""
            {"direct":"{{target.VersionId.Value}}","a/b~c":[null,"{{target.VersionId.Value}}"],
             "objects":[{"ref":"{{target.VersionId.Value}}","text":"{{target.VersionId.Value}}"}]}
            """);
        var next = await f.Save(Next(reading));
        var references = VersionRules.GetReferences(next.Submission, (EntitySchema)schema.Content);
        await Assert.That(references.Select(reference => reference.Position).SequenceEqual([
            "/previousVersionId", "/schemaVersionId", "/observerId", "/content/direct", "/content/a~1b~0c/1", "/content/objects/0/ref"])).IsTrue();
        await Assert.That(references.Count(reference => reference.TargetVersionId == target.VersionId)).IsEqualTo(3);
        await Reject(() => f.Observe(schema, $$"""
            {"direct":"{{target.VersionId.Value}}","a/b~c":[],"objects":[{"ref":"{{VersionId.New().Value}}","text":"x"}]}
            """), "ENTITY-6");
        await Reject(() => f.Observe(schema, $$"""
            {"direct":"{{Guid.NewGuid()}}","a/b~c":[],"objects":[]}
            """), "SCHEMA-9");
    }

    // SCHEMA-9: a declared reference at any depth must hold a VersionId.
    [Test]
    [Arguments("direct")]
    [Arguments("object")]
    [Arguments("array")]
    [Arguments("array-object")]
    public async Task BusinessReferenceValuesMustBeVersionIdsAtEveryDepth(string position)
    {
        using var f = new ModelFixture();
        var schema = await f.Schema(fields: """
            {"direct":{"type":"reference","target":"version","description":"Test field value.","required":false,"nullable":true},
             "object":{"type":"object","description":"Test field value.","required":false,"nullable":false,"fields":{
                "target":{"type":"reference","target":"version","description":"Test field value.","required":true,"nullable":false}}},
             "array":{"type":"array","description":"Test field value.","required":false,"nullable":false,"items":{"type":"reference","target":"version","description":"Test field value.","nullable":false}},
             "array-object":{"type":"array","description":"Test field value.","required":false,"nullable":false,"items":{"type":"object","description":"Test field value.","nullable":false,"fields":{
                "target":{"type":"reference","target":"version","description":"Test field value.","required":true,"nullable":false}}}}}
            """);
        foreach (var id in new[] { Guid.Empty.ToString(), Guid.NewGuid().ToString(), "019e1bbb-ec00-7000-0000-000000000001", "not-a-uuid" })
        {
            var value = System.Text.Json.JsonSerializer.Serialize(id);
            var content = position switch
            {
                "direct" => "{\"direct\":" + value + "}",
                "object" => "{\"object\":{\"target\":" + value + "}}",
                "array" => "{\"array\":[" + value + "]}",
                "array-object" => "{\"array-object\":[{\"target\":" + value + "}]}",
                _ => throw new ArgumentOutOfRangeException(nameof(position)),
            };
            await Reject(() => f.Observe(schema, content), "SCHEMA-9");
        }
        await f.Observe(schema, "{\"direct\":null}");
    }

    // ENTITY-5, OBSERVATION-1, INPUTSET-1
    [Test]
    public async Task CoreReferencesIncludeTheirRolesAndEveryInputPosition()
    {
        var observer = EntityId.New();
        var description = VersionId.New();
        var input = VersionId.New();
        var schema = new EntitySchema("Items", "Items", "items", Json("{}"), "observation", false);
        var observation = New(new Observation(observer, description, "direct", Json("{}"), input), VersionId.New());
        var references = VersionRules.GetReferences(observation, schema);
        await Assert.That(references.Skip(1).SequenceEqual([
            new VersionReference("/observerId", null, observer),
            new VersionReference("/observerVersionId", description),
            new VersionReference("/inputSetVersionId", input)])).IsTrue();
        var inputs = VersionRules.GetReferences(New(new InputSet([description, input])));
        await Assert.That(inputs[1]).IsEqualTo(new VersionReference("/content/members/1", input));
    }
}
