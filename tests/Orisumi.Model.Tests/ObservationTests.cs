using Orisumi.Core;
using static Orisumi.Model.Tests.ModelFixture;

namespace Orisumi.Model.Tests;

public sealed class ObservationTests
{
    // OBSERVATION-1, OBSERVATION-3, OBSERVATION-10, VERSION-12, HISTORY-1, EVIDENCE-3
    [Test]
    public async Task BusinessEntitiesCanShareContentAndOnlyTheSameObserverEntityCanUpdate()
    {
        using var f = new ModelFixture();
        var schema = await f.Schema();
        var observer = await f.Business(schema);
        var content = await f.Business(schema);
        var statement = new Observation(observer.VersionId, content.VersionId, null, null, null, null);
        var first = await f.Save(New(statement));
        await f.Save(New(statement));
        var other = await f.Business(schema);
        await f.Save(New(statement with { ObserverVersionId = other.VersionId }));
        await Reject(() => f.Save(Next(first, statement with { ObserverVersionId = other.VersionId })), "OBSERVATION-10");
        await Reject(() => f.Save(New(statement with { ObserverVersionId = schema.VersionId })), "OBSERVATION-1");
        await Reject(() => f.Save(New(statement with { ContentVersionId = first.VersionId })), "OBSERVATION-1");
        await Reject(() => f.Save(New(statement with { InputSetVersionId = content.VersionId })), "OBSERVATION-1");
        await Reject(() => f.Save(New(statement with { InputSetVersionId = new VersionId(Guid.NewGuid()) })), "VERSION-12");
        var observer2 = await f.Save(Next(observer));
        var inputs = await f.Save(New(new InputSet([content.VersionId])));
        var updated = await f.Save(Next(first, statement with
        {
            ObserverVersionId = observer2.VersionId, ContentVersionId = other.VersionId,
            InputSetVersionId = inputs.VersionId, StartAt = f.Time.Now, EndAt = f.Time.Now.AddHours(1), TimeZone = "Asia/Shanghai",
        }));
        var corrected = await f.Save(Next(updated, statement with { EndAt = f.Time.Now.AddDays(-1), TimeZone = "Etc/UTC" }));
        await Assert.That(((Observation)corrected.Content).StartAt).IsNull();
        await Assert.That(((Observation)first.Content).InputSetVersionId).IsNull();
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
        var business = await f.Business(schema);
        var draft = New(new Observation(business.VersionId, business.VersionId, null,
            start is { } s ? f.Time.Now.AddHours(s) : null, end is { } e ? f.Time.Now.AddHours(e) : null, null));
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
        var business = await f.Business(schema);
        var draft = New(new Observation(business.VersionId, business.VersionId, null, null, null, zone));
        if (valid) await f.Save(draft);
        else await Reject(() => f.Save(draft), "OBSERVATION-9");
    }

    // INPUTSET-1, INPUTSET-2, INPUTSET-3, INPUTSET-4, EVIDENCE-3, EVIDENCE-8, VERSION-12
    [Test]
    public async Task InputsAreExplicitNonemptyUniqueUnorderedVersionsOfAnyCategory()
    {
        using var f = new ModelFixture();
        var schema = await f.Schema();
        var business = await f.Business(schema);
        var observation = await f.Save(New(new Observation(business.VersionId, business.VersionId, null, null, null, null)));
        var input = await f.Save(New(new InputSet([schema.VersionId])));
        var members = new[] { schema.VersionId, business.VersionId, observation.VersionId, input.VersionId };
        var all = await f.Save(New(new InputSet(members)));
        members[0] = VersionId.New();
        await Assert.That(((InputSet)all.Content).Members[0]).IsEqualTo(schema.VersionId);
        await Reject(() => f.Save(New(new InputSet([]))), "INPUTSET-2");
        await Reject(() => f.Save(New(new InputSet([business.VersionId, business.VersionId]))), "INPUTSET-3");
        await Reject(() => f.Save(New(new InputSet([new VersionId(Guid.NewGuid())]))), "VERSION-12");
        var result = await f.Save(New(new Observation(business.VersionId, business.VersionId, all.VersionId, null, null, null)));
        await f.Save(Next(business));
        await Assert.That(((Observation)(await f.Service.ReadVersionAsync(result.VersionId))!.Content).InputSetVersionId).IsEqualTo(all.VersionId);
        await Assert.That(((InputSet)(await f.Service.ReadVersionAsync(all.VersionId))!.Content).Members.Contains(business.VersionId)).IsTrue();
    }
}
