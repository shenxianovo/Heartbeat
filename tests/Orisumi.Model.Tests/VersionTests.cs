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
        var draft = New(new EntitySchema("Items", "Items", "items", Json("{}")));
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
        await Reject(() => f.Save(Next(first)), "VERSION-6");
        await Reject(() => f.Save(Next(second) with { PreviousVersionId = null }), "VERSION-6");
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

    // VERSION-6, VERSION-7: stale new writes conflict; retrying a saved old version still succeeds.
    [Test]
    public async Task StaleWritesCanBeReprocessedAgainstCurrentVersion()
    {
        using var f = new ModelFixture();
        var first = await f.Schema();
        var second = await f.Save(Next(first));
        var current = await f.Save(Next(second));
        foreach (var stale in new[] { first, second })
        {
            var draft = Next(stale);
            await Reject(() => f.Save(draft), "VERSION-6");
            await Assert.That(await f.Service.ReadVersionAsync(draft.VersionId)).IsNull();
            await Assert.That(await f.Save(stale.Submission)).IsEqualTo(stale);
        }
        await Assert.That(await f.Service.ReadCurrentAsync(first.EntityId)).IsEqualTo(current);
        var resolved = await f.Save(Next(current));
        await Assert.That(resolved.PreviousVersionId).IsEqualTo(current.VersionId);
    }

    // VERSION-7, VERSION-2, VERSION-10
    [Test]
    public async Task RetryConfirmsOriginalAndRejectsEveryChangedEnvelopeMember()
    {
        using var f = new ModelFixture();
        var first = await f.Schema();
        await f.Save(Next(first));
        f.Time.Now = f.Time.Now.AddHours(1);
        await Assert.That(await f.Save(first.Submission)).IsEqualTo(first);
        var changes = new[]
        {
            first.Submission with { EntityId = EntityId.New() },
            first.Submission with { PreviousVersionId = first.VersionId },
            first.Submission with { SchemaVersionId = first.VersionId },
            first.Submission with { Content = new InputSet([first.VersionId]) },
            first.Submission with { Content = new EntitySchema("Changed", "Items", "items", Json("{}")) },
        };
        foreach (var change in changes) await Reject(() => f.Save(change), "VERSION-7");
        await Assert.That((await f.Service.ReadVersionsAsync(first.EntityId)).Count).IsEqualTo(2);
    }

    // VERSION-6, SCHEMA-5
    [Test]
    public async Task ConcurrentServicesCannotBranchOrClaimTheSameResource()
    {
        using var f = new ModelFixture();
        var other = new EntityService(f.Store, f.Time);
        var first = await f.Schema();
        async Task<string?> Attempt(EntityService service, VersionSubmission draft)
        {
            return (await service.SubmitAsync([draft])).Rejection?.Rule;
        }
        var writes = await Task.WhenAll(Task.Run(() => Attempt(f.Service, Next(first))), Task.Run(() => Attempt(other, Next(first))));
        await Assert.That(writes.Count(rule => rule is null)).IsEqualTo(1);
        await Assert.That(writes.Single(rule => rule is not null)).IsEqualTo("VERSION-6");
        var content = new EntitySchema("Other", "Other", "other", Json("{}"));
        var claims = await Task.WhenAll(Task.Run(() => Attempt(f.Service, New(content))), Task.Run(() => Attempt(other, New(content))));
        await Assert.That(claims.Count(rule => rule is null)).IsEqualTo(1);
        await Assert.That(claims.Single(rule => rule is not null)).IsEqualTo("SCHEMA-5");
    }

    // VERSION-6: concurrent first submissions share EntityId, but each has its own VersionId.
    [Test]
    public async Task ConcurrentFirstSubmissionsSaveOnlyOneVersion()
    {
        using var f = new ModelFixture();
        var other = new EntityService(f.Store, f.Time);
        var schema = await f.Schema();
        var first = New(new BusinessContent(Json("{}")), schema.VersionId);
        var second = first with { VersionId = VersionId.New() };
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> Submit(EntityService service, VersionSubmission draft)
        {
            await start.Task;
            return (await service.SubmitAsync([draft])).Rejection?.Rule;
        }
        var attempts = new[] { Submit(f.Service, first), Submit(other, second) };
        start.SetResult();
        var outcomes = await Task.WhenAll(attempts);
        await Assert.That(outcomes.Count(rule => rule is null)).IsEqualTo(1);
        await Assert.That(outcomes.Single(rule => rule is not null)).IsEqualTo("VERSION-6");
        var winner = outcomes[0] is null ? first : second;
        var loser = outcomes[0] is null ? second : first;
        var saved = (await f.Service.ReadVersionsAsync(first.EntityId)).Single();
        await Assert.That(saved.VersionId).IsEqualTo(winner.VersionId);
        await Assert.That(saved.PreviousVersionId).IsNull();
        await Assert.That(await f.Service.ReadVersionAsync(loser.VersionId)).IsNull();
        await Assert.That(await f.Save(winner)).IsEqualTo(saved);
    }

    // VERSION-12: every typed VersionId position uses the same UUIDv7 format rule.
    [Test]
    [Arguments("version")]
    [Arguments("previous")]
    [Arguments("schema")]
    [Arguments("observer")]
    [Arguments("content")]
    [Arguments("input-set")]
    [Arguments("member")]
    public async Task InvalidVersionIdFormatsHaveOneRuleAtEveryPosition(string position)
    {
        using var f = new ModelFixture();
        var schema = await f.Schema();
        var business = await f.Business(schema);
        foreach (var id in new[] { Guid.Empty, Guid.NewGuid(), Guid.Parse("019e1bbb-ec00-7000-0000-000000000001") })
        {
            var invalid = new VersionId(id);
            var observation = new Observation(business.VersionId, business.VersionId, null, null, null, null);
            var draft = position switch
            {
                "version" => Next(business) with { VersionId = invalid },
                "previous" => Next(business) with { PreviousVersionId = invalid },
                "schema" => Next(business) with { SchemaVersionId = invalid },
                "observer" => New(observation with { ObserverVersionId = invalid }),
                "content" => New(observation with { ContentVersionId = invalid }),
                "input-set" => New(observation with { InputSetVersionId = invalid }),
                "member" => New(new InputSet([invalid])),
                _ => throw new ArgumentOutOfRangeException(nameof(position)),
            };
            await Reject(() => f.Save(draft), "VERSION-12");
            await Assert.That(await f.Service.ReadVersionAsync(draft.VersionId)).IsNull();
        }
        await Assert.That(await f.Service.ReadCurrentAsync(business.EntityId)).IsEqualTo(business);
    }

    // VERSION-13, ENTITY-6: dependency order, stop at the first rejection, retain prior saves.
    [Test]
    public async Task BatchSavesDependenciesAndStopsAtFirstRejection()
    {
        using var f = new ModelFixture();
        var schema = New(new EntitySchema("Items", "Items", "items", Json("{}")));
        var business = New(new BusinessContent(Json("{}")), schema.VersionId);
        var invalid = New(new InputSet([VersionId.New()]));
        var later = New(new InputSet([business.VersionId]));
        await Reject(() => f.Service.SubmitAsync([schema, business, invalid, later]), "ENTITY-6");
        await Assert.That(await f.Service.ReadVersionAsync(schema.VersionId)).IsNotNull();
        await Assert.That(await f.Service.ReadVersionAsync(business.VersionId)).IsNotNull();
        await Assert.That(await f.Service.ReadVersionAsync(invalid.VersionId)).IsNull();
        await Assert.That(await f.Service.ReadVersionAsync(later.VersionId)).IsNull();
        await f.Save(later);
    }

    // ENTITY-6, ENTITY-4, ENTITY-5
    [Test]
    public async Task ReferencesRejectSelfCyclesAndMissingTargetsButAllowEntityCyclesThroughOlderVersions()
    {
        using var f = new ModelFixture();
        var first = New(new InputSet([VersionId.New()]));
        var second = New(new InputSet([first.VersionId]));
        first = first with { Content = new InputSet([second.VersionId]) };
        await Reject(() => f.Service.SubmitAsync([first, second]), "ENTITY-6");
        await Reject(() => f.Save(first with { Content = new InputSet([first.VersionId]) }), "ENTITY-6");
        var schema = await f.Schema();
        var a = await f.Save(New(new InputSet([schema.VersionId])));
        var b = await f.Save(New(new InputSet([a.VersionId])));
        var a2 = await f.Save(Next(a, new InputSet([b.VersionId])));
        await Assert.That(((InputSet)a.Content).Members[0]).IsEqualTo(schema.VersionId);
        await Assert.That(((InputSet)a2.Content).Members[0]).IsEqualTo(b.VersionId);
    }
}
