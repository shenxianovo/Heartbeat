using Orisumi.Application.Entities;
using Orisumi.Core;
using static Orisumi.Model.Tests.ModelFixture;

namespace Orisumi.Model.Tests;

public sealed class VersionTests
{
    // ENTITY-1, ENTITY-2, IDENTITY-1
    [Test]
    public async Task WriterIdsArePreservedAndMustBeUuid7()
    {
        using var f = new ModelFixture();
        var draft = New(new EntitySchema("Items", "Items", "items", Json("{}"), "observation", false));
        await Assert.That(EntityId.New().Value.Version).IsEqualTo(7);
        await Assert.That(VersionId.New().Value.Version).IsEqualTo(7);
        foreach (var invalid in new[] { Guid.Empty, Guid.NewGuid(), Guid.Parse("019e1bbb-ec00-7000-0000-000000000001") })
            await Reject(() => f.Save(draft with { EntityId = new(invalid) }), "ENTITY-2");
        var saved = await f.Save(draft);
        await Assert.That(saved.EntityId).IsEqualTo(draft.EntityId);
        await Assert.That(saved.VersionId).IsEqualTo(draft.VersionId);
    }

    // VERSION-2, VERSION-4, VERSION-5, VERSION-6, VERSION-8, VERSION-10, VERSION-11, HISTORY-3
    [Test]
    public async Task VersionsFormOneImmutableChainEvenWithEqualTimesAndContent()
    {
        using var f = new ModelFixture();
        var first = await f.Schema();
        var second = await f.Save(Next(first));
        await Assert.That(second.VersionId == first.VersionId).IsFalse();
        await Assert.That(second.RecordedAt).IsEqualTo(first.RecordedAt);
        await Assert.That(first.RecordedAt).IsEqualTo(f.Time.Now);
        await Assert.That(await f.Service.ReadCurrentAsync(first.EntityId)).IsEqualTo(second);
        await Assert.That((await f.Service.ReadVersionsAsync(first.EntityId)).Select(v => v.VersionId).SequenceEqual([first.VersionId, second.VersionId])).IsTrue();
        await Reject(() => f.Save(Next(first)), "VERSION-4");
        await Reject(() => f.Save(Next(second) with { PreviousVersionId = null }), "VERSION-4");
        await Reject(() => f.Save(Next(second) with { EntityId = EntityId.New() }), "VERSION-4");
        await Assert.That(await f.Service.ReadVersionAsync(first.VersionId)).IsEqualTo(first);
        f.Time.Now = f.Time.Now.AddDays(-1);
        var third = await f.Save(Next(second));
        await Assert.That(third.RecordedAt < second.RecordedAt).IsTrue();
        await Assert.That(await f.Service.ReadCurrentAsync(first.EntityId)).IsEqualTo(third);
    }

    // VERSION-4: an invalid predecessor is distinct from a stale same-entity write.
    [Test]
    [Arguments("first-existing")]
    [Arguments("first-missing")]
    [Arguments("missing")]
    [Arguments("other-entity")]
    [Arguments("self")]
    public async Task InvalidPredecessorsReportChainViolation(string scenario)
    {
        using var f = new ModelFixture();
        var first = await f.Schema();
        var current = await f.Save(Next(first));
        var other = await f.Schema("other");
        var draft = Next(current);
        draft = scenario switch
        {
            "first-existing" => draft with { EntityId = EntityId.New() },
            "first-missing" => draft with { EntityId = EntityId.New(), PreviousVersionId = VersionId.New() },
            "missing" => draft with { PreviousVersionId = VersionId.New() },
            "other-entity" => draft with { PreviousVersionId = other.VersionId },
            "self" => draft with { PreviousVersionId = draft.VersionId },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        await Reject(() => f.Save(draft), "VERSION-4");
        await Assert.That(await f.Service.ReadVersionAsync(draft.VersionId)).IsNull();
        await Assert.That(await f.Service.ReadCurrentAsync(first.EntityId)).IsEqualTo(current);
    }

    // VERSION-13, ENTITY-6: dependency order; one rejection saves nothing from the submission.
    [Test]
    public async Task BatchSavesDependenciesInOrderAndNothingWhenOneIsRejected()
    {
        using var f = new ModelFixture();
        var schema = New(new EntitySchema("Items", "Items", "items", Json("{}"), "observation", false));
        var reading = New(f.Content(Json("{}")), schema.VersionId);
        var invalid = New(new InputSet([VersionId.New()]));
        var later = New(new InputSet([reading.VersionId]));
        await Reject(() => f.SaveMany(schema, reading, invalid, later), "ENTITY-6");
        foreach (var version in new[] { schema, reading, invalid, later })
            await Assert.That(await f.Service.ReadVersionAsync(version.VersionId)).IsNull();
        await f.SaveMany(schema, reading, later);
        await Assert.That(await f.Service.ReadVersionAsync(later.VersionId)).IsNotNull();
    }

    // ENTITY-6, ENTITY-4, ENTITY-5
    [Test]
    public async Task ReferencesRejectSelfCyclesAndMissingTargetsButAllowEntityCyclesThroughOlderVersions()
    {
        using var f = new ModelFixture();
        var first = New(new InputSet([VersionId.New()]));
        var second = New(new InputSet([first.VersionId]));
        first = first with { Content = new InputSet([second.VersionId]) };
        await Reject(() => f.SaveMany(first, second), "ENTITY-6");
        await Reject(() => f.Save(first with { Content = new InputSet([first.VersionId]) }), "ENTITY-6");
        var schema = await f.Schema();
        var a = await f.Save(New(new InputSet([schema.VersionId])));
        var b = await f.Save(New(new InputSet([a.VersionId])));
        var a2 = await f.Save(Next(a, new InputSet([b.VersionId])));
        await Assert.That(((InputSet)a.Content).Members[0]).IsEqualTo(schema.VersionId);
        await Assert.That(((InputSet)a2.Content).Members[0]).IsEqualTo(b.VersionId);
    }
}
