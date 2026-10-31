using Orisumi.Core;
using static Orisumi.Model.Tests.ModelFixture;

namespace Orisumi.Model.Tests;

public sealed class ObservationTests
{
    [Test]
    public async Task ObservationHasOneAuthorAndOptionalDescriptionAndInputs()
    {
        using var f = new ModelFixture();
        var schema = await f.Schema();
        var description = await f.Observe(schema);
        var first = await f.Save(New(f.Content(Json("{}")) with { ObserverVersionId = description.VersionId }, schema.VersionId));
        var other = EntityId.New();
        await f.Store.ExecuteAsync(async (tx, token) => { await tx.CreateThingAsync(other, Orisumi.Application.Entities.BuiltInSchemas.Program, token); return true; });
        await Reject(() => f.Save(Next(first, ((Observation)first.Content) with { ObserverId = other })), "OBSERVATION-1");
        await Reject(() => f.Save(Next(first, ((Observation)first.Content) with { ObserverId = other, ObserverVersionId = null })), "OBSERVATION-10");
        await Reject(() => f.Save(Next(first, ((Observation)first.Content) with { InputSetVersionId = schema.VersionId })), "OBSERVATION-1");
        await Reject(() => f.Save(Next(first, ((Observation)first.Content) with { Formation = "unknown" })), "OBSERVATION-11");
        var input = await f.Save(New(new InputSet([description.VersionId])));
        var next = await f.Save(Next(first, ((Observation)first.Content) with { InputSetVersionId = input.VersionId }));
        await Assert.That(((Observation)next.Content).InputSetVersionId).IsEqualTo(input.VersionId);
    }

    // OBSERVATION-5, OBSERVATION-6, OBSERVATION-7, OBSERVATION-8, OBSERVATION-9
    [Test]
    [Arguments(null, null, true)]
    [Arguments(0, null, true)]
    [Arguments(null, 0, true)]
    [Arguments(0, 0, true)]
    [Arguments(0, 1, true)]
    [Arguments(1, 0, false)]
    public async Task FactTimeAllowsUnknownPointAndIntervalButNotReversedBounds(int? start, int? end, bool valid)
    {
        using var f = new ModelFixture();
        var schema = await f.Schema();
        var draft = New(f.Content(Json("{}")) with { StartAt = start is { } s ? f.Time.Now.AddHours(s) : null, EndAt = end is { } e ? f.Time.Now.AddHours(e) : null }, schema.VersionId);
        if (valid)
        {
            var saved = await f.Save(draft);
            await Assert.That(saved.Content).IsEqualTo(draft.Content);
        }
        else await Reject(() => f.Save(draft), "OBSERVATION-6");
    }

    // OBSERVATION-9
    [Test]
    [Arguments(null, true)]
    [Arguments("Asia/Shanghai", true)]
    [Arguments("Etc/UTC", true)]
    [Arguments("UTC", true)]
    [Arguments("America/New_York", true)]
    [Arguments("China Standard Time", false)]
    [Arguments("", false)]
    [Arguments("Mars/Olympus", false)]
    public async Task TimeZoneMustBeIanaOrUnknown(string? zone, bool valid)
    {
        using var f = new ModelFixture();
        var schema = await f.Schema();
        var draft = New(f.Content(Json("{}")) with { TimeZone = zone }, schema.VersionId);
        if (valid) await f.Save(draft);
        else await Reject(() => f.Save(draft), "OBSERVATION-9");
    }

    // INPUTSET-1, INPUTSET-2, INPUTSET-3, INPUTSET-4, EVIDENCE-3, EVIDENCE-8, VERSION-12
    [Test]
    public async Task InputsAreExplicitNonemptyUniqueUnorderedVersionsOfAnyCategory()
    {
        using var f = new ModelFixture();
        var schema = await f.Schema();
        var reading = await f.Observe(schema);
        var observation = await f.Save(New(f.Content(Json("{}")), schema.VersionId));
        var input = await f.Save(New(new InputSet([schema.VersionId])));
        var members = new[] { schema.VersionId, reading.VersionId, observation.VersionId, input.VersionId };
        var all = await f.Save(New(new InputSet(members)));
        members[0] = VersionId.New();
        await Assert.That(((InputSet)all.Content).Members[0]).IsEqualTo(schema.VersionId);
        await Reject(() => f.Save(New(new InputSet([]))), "INPUTSET-2");
        await Reject(() => f.Save(New(new InputSet([reading.VersionId, reading.VersionId]))), "INPUTSET-3");
        await Reject(() => f.Save(New(new InputSet([new VersionId(Guid.NewGuid())]))), "VERSION-12");
        var result = await f.Save(New(f.Content(Json("{}")) with { InputSetVersionId = all.VersionId }, schema.VersionId));
        await f.Save(Next(reading));
        await Assert.That(((Observation)(await f.Service.ReadVersionAsync(result.VersionId))!.Content).InputSetVersionId).IsEqualTo(all.VersionId);
        await Assert.That(((InputSet)(await f.Service.ReadVersionAsync(all.VersionId))!.Content).Members.Contains(reading.VersionId)).IsTrue();
    }
}
