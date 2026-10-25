using Heartbeat.Core;
using static Heartbeat.Model.Tests.ModelFixture;

namespace Heartbeat.Model.Tests;

public sealed class EntityReferenceTests
{
    // ENTITY-5, SCHEMA-9, SCHEMA-14, VERSION-12
    [Test]
    public async Task NestedReferencesKeepEveryPositionAndIgnoreOrdinaryStrings()
    {
        using var f = new ModelFixture();
        var target = await f.Schema("targets");
        var schema = await f.Schema("nested", """
            {"direct":{"type":"reference","required":true,"nullable":false},
             "a/b~c":{"type":"array","required":true,"nullable":false,"items":{"type":"reference","nullable":true}},
             "objects":{"type":"array","required":true,"nullable":false,"items":{"type":"object","nullable":false,"fields":{
                "ref":{"type":"reference","required":true,"nullable":false},
                "text":{"type":"string","required":true,"nullable":false}}}}}
            """);
        var business = await f.Business(schema, $$"""
            {"direct":"{{target.VersionId.Value}}","a/b~c":[null,"{{target.VersionId.Value}}"],
             "objects":[{"ref":"{{target.VersionId.Value}}","text":"{{target.VersionId.Value}}"}]}
            """);
        var next = await f.Save(Next(business));
        var references = VersionRules.GetReferences(next.Submission, (EntitySchema)schema.Content);
        await Assert.That(references.Select(reference => reference.Position).SequenceEqual([
            "/previousVersionId", "/schemaVersionId", "/content/direct", "/content/a~1b~0c/1", "/content/objects/0/ref"])).IsTrue();
        await Assert.That(references.Count(reference => reference.Target == target.VersionId)).IsEqualTo(3);
        await Reject(() => f.Business(schema, $$"""
            {"direct":"{{target.VersionId.Value}}","a/b~c":[],"objects":[{"ref":"{{VersionId.New().Value}}","text":"x"}]}
            """), "ENTITY-6");
        await Reject(() => f.Business(schema, $$"""
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
            {"direct":{"type":"reference","required":false,"nullable":true},
             "object":{"type":"object","required":false,"nullable":false,"fields":{
                "target":{"type":"reference","required":true,"nullable":false}}},
             "array":{"type":"array","required":false,"nullable":false,"items":{"type":"reference","nullable":false}},
             "array-object":{"type":"array","required":false,"nullable":false,"items":{"type":"object","nullable":false,"fields":{
                "target":{"type":"reference","required":true,"nullable":false}}}}}
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
            await Reject(() => f.Business(schema, content), "SCHEMA-9");
        }
        await f.Business(schema, "{\"direct\":null}");
    }

    // ENTITY-5, OBSERVATION-1, INPUTSET-1
    [Test]
    public async Task CoreReferencesIncludeTheirRolesAndEveryInputPosition()
    {
        var observer = VersionId.New();
        var content = VersionId.New();
        var input = VersionId.New();
        var observation = New(new Observation(observer, content, input, null, null, null));
        var references = VersionRules.GetReferences(observation);
        await Assert.That(references.SequenceEqual([
            new VersionReference("/content/observerVersionId", observer),
            new VersionReference("/content/contentVersionId", content),
            new VersionReference("/content/inputSetVersionId", input)])).IsTrue();
        var inputs = VersionRules.GetReferences(New(new InputSet([observer, content])));
        await Assert.That(inputs[1]).IsEqualTo(new VersionReference("/content/members/1", content));
        await Assert.That(VersionRules.GetReferences(New(new EntitySchema(observer.Value.ToString(), "Description", "items", Json("{}")))).Count).IsEqualTo(0);
    }
}
