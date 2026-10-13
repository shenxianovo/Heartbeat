using System.Text.Json;
using Heartbeat.Observers.Sources;
using Heartbeat.Observers.Sources.Communication;
using Heartbeat.Observers.Sources.Gmail;
using Heartbeat.Observers.Sources.Local;
using Heartbeat.Observers.Sources.PhysicalCalendar;
using Heartbeat.Observers.Sources.Telemetry;

namespace Heartbeat.Model.Tests;

public sealed class SourceCoverageTests
{
    [Test]
    public async Task EveryOriginalScenarioHasOneHonestSourceStatusAndKnownAdapters()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "scenario-coverage.json")));
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var expected = "ABCD".SelectMany(group => Enumerable.Range(1, 25).Select(n => $"{group}{n:00}")).ToHashSet(StringComparer.Ordinal);
        await Assert.That(cases.Length).IsEqualTo(100);
        await Assert.That(cases.Select(c => c.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal).SetEquals(expected)).IsTrue();
        IRecordSource[] adapters = [new GitSource(), new FileSource(), new ProcessSource(), new GraphMessagesSource(), new SlackEventsSource(),
            new GmailMessagesSource(), new GmailDraftsSource(), new GmailHistorySource(), new OtlpJsonSource(), new CloudEventsJsonSource(),
            new DebeziumJsonSource(), new ICalendarSource(), new GpxSource()];
        var known = adapters.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        var statuses = new HashSet<string>(StringComparer.Ordinal) { "model-only", "native-partial", "native-targeted", "related-only", "alternate-format-partial", "existing-native-unverified" };
        foreach (var item in cases)
        {
            var status = item.GetProperty("semanticStatus").GetString()!;
            await Assert.That(statuses.Contains(status)).IsTrue();
            await Assert.That(string.IsNullOrWhiteSpace(item.GetProperty("remainingGap").GetString())).IsFalse();
            foreach (var adapter in item.GetProperty("adapters").EnumerateArray()) await Assert.That(known.Contains(adapter.GetString()!)).IsTrue();
            if (status is "native-targeted" or "native-partial" or "alternate-format-partial")
                await Assert.That(item.GetProperty("nativeTests").GetArrayLength() > 0).IsTrue();
            using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", Path.GetFileName(item.GetProperty("modelFixture").GetString()!))));
            await Assert.That(fixtures.RootElement.EnumerateArray().Any(f => f.GetProperty("id").GetString() == item.GetProperty("id").GetString())).IsTrue();
        }
    }
}
