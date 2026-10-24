using Heartbeat.Core;
using static Heartbeat.Model.Tests.ModelFixture;

namespace Heartbeat.Model.Tests;

public sealed class DeletionTests
{
    // ENTITY-1, ENTITY-10, VERSION-9, HISTORY-5, HISTORY-6, HISTORY-7, HISTORY-8, EVIDENCE-8
    [Test]
    public async Task DeleteRemovesAllVersionsOfOnlySelectedEntitiesAndPermanentlyOccupiesIds()
    {
        using var f = new ModelFixture();
        var schema = await f.Schema();
        var target = await f.Business(schema);
        var second = await f.Save(Next(target));
        var inputs = await f.Save(New(new InputSet([target.VersionId, second.VersionId])));
        var observer = await f.Business(schema);
        var observation = await f.Save(New(new Observation(observer.VersionId, target.VersionId, inputs.VersionId, null, null, null)));
        await f.Service.DeleteAsync([target.EntityId]);
        await Assert.That(await f.Service.ReadVersionAsync(target.VersionId)).IsNull();
        await Assert.That(await f.Service.ReadVersionAsync(second.VersionId)).IsNull();
        await Assert.That(await f.Service.ReadCurrentAsync(target.EntityId)).IsNull();
        await Assert.That((await f.Service.ReadVersionsAsync(target.EntityId)).Count).IsEqualTo(0);
        await Assert.That(await f.Service.ReadVersionAsync(inputs.VersionId)).IsEqualTo(inputs);
        await Assert.That(await f.Service.ReadVersionAsync(observation.VersionId)).IsEqualTo(observation);
        await Reject(() => f.Save(target.Submission), "HISTORY-7");
        await Reject(() => f.Save(Next(second)), "HISTORY-7");
        await Reject(() => f.Save(target.Submission with { EntityId = EntityId.New() }), "VERSION-9");
        await Reject(() => f.Save(New(new InputSet([target.VersionId]))), "ENTITY-6");
        // VERSION-7 confirms the original reference checks even after their target disappears.
        await Assert.That(await f.Save(inputs.Submission)).IsEqualTo(inputs);
        await Assert.That(await f.Save(observation.Submission)).IsEqualTo(observation);
        await f.Service.DeleteAsync([target.EntityId, observation.EntityId, inputs.EntityId, observer.EntityId, schema.EntityId]);
        foreach (var entity in new[] { schema, observer, inputs, observation })
        {
            await Assert.That(await f.Service.ReadVersionAsync(entity.VersionId)).IsNull();
            await Reject(() => f.Save(entity.Submission), "HISTORY-7");
        }
    }

    // HISTORY-9
    [Test]
    public async Task AnySurvivingBusinessVersionPreventsSchemaDeletion()
    {
        using var f = new ModelFixture();
        var schema = await f.Schema();
        var business = await f.Business(schema);
        var schema2 = await f.Save(Next(schema));
        await f.Save(Next(business) with { SchemaVersionId = schema2.VersionId });
        await Reject(() => f.Service.DeleteAsync([schema.EntityId]), "HISTORY-9");
        await Assert.That(await f.Service.ReadVersionAsync(schema.VersionId)).IsNotNull();
        await Assert.That(await f.Service.ReadVersionAsync(schema2.VersionId)).IsNotNull();
        await f.Service.DeleteAsync([business.EntityId, schema.EntityId]);
        await Assert.That(await f.Service.ReadVersionAsync(schema.VersionId)).IsNull();
        await Assert.That(await f.Service.ReadVersionAsync(schema2.VersionId)).IsNull();
    }

    // HISTORY-8, HISTORY-9: references outside business schemaVersionId do not prevent deletion.
    [Test]
    public async Task InputReferenceDoesNotKeepAnOtherwiseUnusedSchemaAlive()
    {
        using var f = new ModelFixture();
        var schema = await f.Schema();
        var inputs = await f.Save(New(new InputSet([schema.VersionId])));
        await f.Service.DeleteAsync([schema.EntityId]);
        await Assert.That(await f.Service.ReadVersionAsync(inputs.VersionId)).IsEqualTo(inputs);
        await Assert.That(await f.Service.ReadVersionAsync(schema.VersionId)).IsNull();
    }

    // ENTITY-10: reading and deleting an unknown identity do not create it or a tombstone.
    [Test]
    public async Task MissingReadsAndRepeatedDeletesAreHarmless()
    {
        using var f = new ModelFixture();
        var draft = New(new EntitySchema("Items", "Items", "items", Json("{}")));
        await Assert.That(await f.Service.ReadVersionAsync(draft.VersionId)).IsNull();
        await Assert.That(await f.Service.ReadCurrentAsync(draft.EntityId)).IsNull();
        await Assert.That((await f.Service.ReadVersionsAsync(draft.EntityId)).Count).IsEqualTo(0);
        await f.Service.DeleteAsync([draft.EntityId]);
        await f.Save(draft);
        await f.Service.DeleteAsync([draft.EntityId, draft.EntityId]);
    }
}
