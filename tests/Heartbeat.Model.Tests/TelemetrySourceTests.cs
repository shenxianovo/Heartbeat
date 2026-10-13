using System.Text.Json;
using Heartbeat.Observers.Sources;
using Heartbeat.Observers.Sources.Telemetry;

namespace Heartbeat.Model.Tests;

public sealed class TelemetrySourceTests
{
    [Test]
    [Property("caseId", "B06")]
    public async Task NativeSpanKeepsMissingRemoteParentAsAReference()
    {
        var records = await Fixture(new OtlpJsonSource(), "otlp-traces.json");
        var span = records.Single(x => x.Kind == "otlp-span");
        await Assert.That(span.References["parentSpanId"].Kind).IsEqualTo("otlp-span");
        await Assert.That(records.Any(x => x.NativeKey == span.References["parentSpanId"].NativeKey)).IsFalse();
        await Assert.That(span.Properties.GetProperty("resourceJson").GetString()!.Contains("checkout", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    [Property("caseId", "B07")]
    public async Task NativeLinksKeepOrderDuplicatesAttributesAndBothEnds()
    {
        var records = await Fixture(new OtlpJsonSource(), "otlp-traces.json");
        var span = records.Single(x => x.Kind == "otlp-span");
        var links = records.Where(x => x.Kind == "otlp-span-link").ToArray();
        await Assert.That(links.Length).IsEqualTo(2);
        await Assert.That(links[0].NativeKey == links[1].NativeKey).IsFalse();
        await Assert.That(links[0].References["spanId"].NativeKey).IsEqualTo(span.NativeKey);
        await Assert.That(links[0].References["linkedSpanId"]).IsEqualTo(links[1].References["linkedSpanId"]);
        await Assert.That(links[1].Properties.GetProperty("ordinal").GetInt32()).IsEqualTo(1);
        await Assert.That(Attribute(links[0], "attributes").Contains("retry", StringComparison.Ordinal)).IsTrue();
        await Assert.That(links[0].StartAt).IsNull();
    }

    [Test]
    [Property("caseId", "B08")]
    public async Task SubTickSpanBoundariesRemainExactInTypedSourceFields()
    {
        var records = await Fixture(new OtlpJsonSource(), "otlp-traces.json");
        var span = records[0];
        await Assert.That(span.StartAt).IsEqualTo(span.EndAt);
        await Assert.That(span.Properties.GetProperty("startTimeUnixNano").GetString()).IsEqualTo("1700000000000000001");
        await Assert.That(span.Properties.GetProperty("endTimeUnixNano").GetString()).IsEqualTo("1700000000000000099");
        using var extension = JsonDocument.Parse(Attribute(span, "vendorFuture"));
        await Assert.That(extension.RootElement.GetProperty("nested")[2].GetInt32()).IsEqualTo(99);
    }

    [Test]
    [Property("caseId", "B09")]
    public async Task EventArrayOrderDoesNotRewriteSourceEventTimesOrMissingEnd()
    {
        var records = await Fixture(new OtlpJsonSource(), "otlp-traces.json");
        var events = records.Where(x => x.Kind == "otlp-span-event").ToArray();
        await Assert.That(events[0].Properties.GetProperty("name").GetString()).IsEqualTo("late");
        await Assert.That(events[1].Properties.GetProperty("timeUnixNano").GetString()).IsEqualTo("1700000000000000002");
        var json = await File.ReadAllTextAsync(FixturePath("otlp-traces.json"));
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["resourceSpans"]![0]!["scopeSpans"]![0]!["spans"]![0]!.AsObject().Remove("endTimeUnixNano");
        var open = (await Parse(new OtlpJsonSource(), node.ToJsonString()))[0];
        await Assert.That(open.EndAt).IsNull();
        await Assert.That(open.NativeKey).IsEqualTo(records[0].NativeKey);
        await Assert.That(open.EvidenceKey == records[0].EvidenceKey).IsFalse();
    }

    [Test]
    [Property("caseId", "B10")]
    public async Task DroppedCountsRemainEvidenceAndSpanReplayUsesNativeIdentity()
    {
        var first = await Fixture(new OtlpJsonSource(), "otlp-traces.json");
        var second = await Read(new OtlpJsonSource(), new(FixturePath("otlp-traces.json"), "collector-one", "another-capture"));
        await Assert.That(first[0].EvidenceKey).IsEqualTo(second[0].EvidenceKey);
        await Assert.That(first[0].Properties.GetProperty("droppedEventsCount").GetInt64()).IsEqualTo(4L);
    }

    [Test]
    [Property("caseId", "B22")]
    public async Task LogsUseEventTimeNeverObservedTimeAsFactTime()
    {
        var records = await Fixture(new OtlpJsonSource(), "otlp-logs.json");
        await Assert.That(records[0].StartAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddTicks(17000000000000000));
        await Assert.That(records[0].Properties.GetProperty("observedTimeUnixNano").GetString()).IsEqualTo("1700000000999999999");
        await Assert.That(records[1].StartAt).IsNull();
        await Assert.That(records[1].EndAt).IsNull();
        await Assert.That(records[0].References.ContainsKey("spanId")).IsTrue();
    }

    [Test]
    [Property("caseId", "C23")]
    public async Task CumulativeSumKeepsWindowUnitAndLosslessInteger()
    {
        var sum = (await Fixture(new OtlpJsonSource(), "otlp-metrics.json"))[0];
        await Assert.That(sum.Properties.GetProperty("aggregationTemporality").GetInt32()).IsEqualTo(2);
        await Assert.That(sum.Properties.GetProperty("unit").GetString()).IsEqualTo("{request}");
        await Assert.That(sum.Properties.GetProperty("valueInteger").GetString()).IsEqualTo("9007199254740993");
        await Assert.That(sum.Properties.GetProperty("startTimeUnixNano").GetString()).IsEqualTo("1700000000000000001");
        await Assert.That(sum.StartAt < sum.EndAt).IsTrue();
    }

    [Test]
    [Property("caseId", "C24")]
    public async Task GaugeIsAPointAndDoesNotInventAWindowStart()
    {
        var gauge = (await Fixture(new OtlpJsonSource(), "otlp-metrics.json"))[1];
        await Assert.That(gauge.Properties.GetProperty("startTimeUnixNano").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(gauge.StartAt).IsEqualTo(gauge.EndAt);
        await Assert.That(gauge.Properties.GetProperty("valueDouble").GetDouble()).IsEqualTo(21.25);
    }

    [Test]
    [Property("caseId", "D20")]
    public async Task CloudEventsAtSameTimeKeepSourceAndEventIdentity()
    {
        var records = await Fixture(new CloudEventsJsonSource(), "cloudevents.json");
        await Assert.That(records[0].StartAt).IsEqualTo(records[1].StartAt);
        await Assert.That(records[0].NativeKey == records[1].NativeKey).IsFalse();
        await Assert.That(records[0].Properties.GetProperty("exactTime").GetString()).IsEqualTo("2026-10-12T01:02:03.123456789Z");
        await Assert.That(Attribute(records[0], "tenant")).IsEqualTo("\"one\"");
        var unknown = await Parse(new CloudEventsJsonSource(), """{"specversion":"1.0","source":"/orders","id":"unknown","type":"test"}""");
        await Assert.That(unknown[0].StartAt).IsNull();
    }

    [Test]
    [Property("caseId", "D01")]
    public async Task CdcUsesSourceClockAndTransactionOrderIndependentOfDelivery()
    {
        var records = await Fixture(new DebeziumJsonSource(), "debezium.json");
        await Assert.That(records[0].Properties.GetProperty("transactionOrder").GetInt64()).IsEqualTo(2L);
        await Assert.That(records[0].Properties.GetProperty("sourceTime").GetString()).IsEqualTo("1700000000000000001");
        await Assert.That(records[0].Properties.GetProperty("processingTime").GetString()).IsEqualTo("1700000099999");
        await Assert.That(records[0].Properties.GetProperty("processingTimeUnit").GetString()).IsEqualTo("ms");
        await Assert.That(records[0].References["transactionEntityId"].Kind).IsEqualTo("debezium-transaction");
        await Assert.That(records[0].StartAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddTicks(17000000000000000));
        var reversed = JsonDocument.Parse(await File.ReadAllTextAsync(FixturePath("debezium.json"))).RootElement.EnumerateArray().Reverse().ToArray();
        var replay = await Parse(new DebeziumJsonSource(), JsonSerializer.Serialize(reversed));
        await Assert.That(replay[1].NativeKey).IsEqualTo(records[0].NativeKey);
    }

    [Test]
    [Property("caseId", "D02")]
    public async Task CdcDeleteIsAChangeAndKafkaTombstoneIsRejected()
    {
        var records = await Fixture(new DebeziumJsonSource(), "debezium.json");
        await Assert.That(records[1].Properties.GetProperty("operation").GetString()).IsEqualTo("d");
        await Assert.That(records[1].Properties.GetProperty("afterJson").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(async () => { await Parse(new DebeziumJsonSource(), "null"); }).Throws<InvalidDataException>();
        await Assert.That(async () => { await Parse(new DebeziumJsonSource(), "{\"payload\":null}"); }).Throws<InvalidDataException>();
    }

    [Test]
    public async Task CdcUpdateKeepsBothRowImagesAndNativeExtension()
    {
        var record = (await Fixture(new DebeziumJsonSource(), "debezium.json"))[0];
        using var before = JsonDocument.Parse(record.Properties.GetProperty("beforeJson").GetString()!);
        using var after = JsonDocument.Parse(record.Properties.GetProperty("afterJson").GetString()!);
        await Assert.That(before.RootElement.GetProperty("amount").GetInt32()).IsEqualTo(1);
        await Assert.That(after.RootElement.GetProperty("amount").GetInt32()).IsEqualTo(2);
        await Assert.That(Attribute(record, "vendorFuture")).IsEqualTo("true");
    }

    [Test]
    [Arguments("{}")]
    [Arguments("{\"resourceSpans\":{}}")]
    [Arguments("{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{\"name\":\"missing IDs\"}]}]}]}")]
    [Arguments("{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":[{\"timeUnixNano\":\"yesterday\"}]}]}]}")]
    [Arguments("{\"resourceLogs\":[],\"resourceMetrics\":[]}")]
    public async Task OtlpRejectsWrongShapeMissingIdsAndMalformedTime(string json) =>
        await Assert.That(async () => { await Parse(new OtlpJsonSource(), json); }).Throws<InvalidDataException>();

    [Test]
    [Arguments("{\"specversion\":\"1.0\",\"source\":\"/orders\",\"type\":\"test\"}")]
    [Arguments("{\"specversion\":\"1.0\",\"source\":\"/orders\",\"id\":\"1\",\"type\":\"test\",\"time\":\"tomorrow\"}")]
    [Arguments("{\"specversion\":\"1.0\",\"source\":\"/orders\",\"id\":\"1\",\"type\":\"test\",\"time\":\"2026-01-01T00:00:00\"}")]
    [Arguments("{\"specversion\":\"1.0\",\"source\":\"/orders\",\"id\":\"1\",\"type\":\"test\",\"data\":{},\"data_base64\":\"aGk=\"}")]
    public async Task CloudEventsRejectsMissingIdsAndInvalidTime(string json) =>
        await Assert.That(async () => { await Parse(new CloudEventsJsonSource(), json); }).Throws<InvalidDataException>();

    [Test]
    public async Task CdcRejectsMalformedSourceTimeAndMissingPosition()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(FixturePath("debezium.json")))![0]!.DeepClone();
        node["payload"]!["source"]!["ts_ns"] = "bad";
        await Assert.That(async () => { await Parse(new DebeziumJsonSource(), node.ToJsonString()); }).Throws<InvalidDataException>();
        node["payload"]!["source"]!.AsObject().Remove("ts_ns");
        node["payload"]!["source"]!.AsObject().Remove("lsn");
        await Assert.That(async () => { await Parse(new DebeziumJsonSource(), node.ToJsonString()); }).Throws<InvalidDataException>();
    }

    [Test]
    public async Task CancellationStopsAllTelemetrySourcesBeforeReading()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        foreach (var source in new IRecordSource[] { new OtlpJsonSource(), new CloudEventsJsonSource(), new DebeziumJsonSource() })
            await Assert.That(async () => { await Read(source, new("nonexistent", "scope", "capture"), cancel.Token); }).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task ReorderedNativeObjectsReplayThroughTheDurableStore()
    {
        foreach (var (source, file) in new (IRecordSource, string)[]
        {
            (new CloudEventsJsonSource(), "cloudevents.json"), (new OtlpJsonSource(), "otlp-traces.json"),
            (new OtlpJsonSource(), "otlp-logs.json"), (new OtlpJsonSource(), "otlp-metrics.json"),
            (new DebeziumJsonSource(), "debezium.json")
        })
        {
            var original = await Fixture(source, file);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(FixturePath(file)));
            var reordered = await Parse(source, JsonSerializer.Serialize(ReverseObjectKeys(document.RootElement)));
            var directory = Directory.CreateTempSubdirectory("telemetry-replay-");
            try
            {
                using var store = new SourceStore(Path.Combine(directory.FullName, "state.sqlite"));
                var initial = store.EnqueueBatch("collector-one", original);
                var replay = store.EnqueueBatch("collector-one", reordered);
                await Assert.That(replay.All(x => x.Replayed)).IsTrue();
                await Assert.That(initial.Select(x => x.ObservationId).SequenceEqual(replay.Select(x => x.ObservationId))).IsTrue();
                var otherScope = store.EnqueueBatch("collector-two", reordered);
                await Assert.That(otherScope[0].SourceEntityId == initial[0].SourceEntityId).IsFalse();
            }
            finally { directory.Delete(true); }
        }
    }

    [Test]
    public async Task CloudEventsNullContextAttributeMeansUnsetButNullDataIsPreserved()
    {
        const string json = """{"specversion":"1.0","source":"/orders","id":"null-time","type":"test","time":null,"subject":null,"data":null}""";
        var record = (await Parse(new CloudEventsJsonSource(), json))[0];
        await Assert.That(record.StartAt).IsNull();
        await Assert.That(Attribute(record, "data")).IsEqualTo("null");
        await Assert.That(record.Properties.GetProperty("attributes").EnumerateArray().Any(x => x.GetProperty("name").GetString() == "time")).IsFalse();
    }

    [Test]
    public async Task CdcWithoutTransactionOrderDoesNotCollapseIndistinguishableOccurrences()
    {
        var item = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(FixturePath("debezium.json")))![0]!.DeepClone();
        item["payload"]!.AsObject().Remove("transaction");
        var records = await Parse(new DebeziumJsonSource(), "[" + item.ToJsonString() + "," + item.ToJsonString() + "]");
        await Assert.That(records.Count).IsEqualTo(2);
        await Assert.That(records[0].NativeKey == records[1].NativeKey).IsFalse();
        await Assert.That(records[0].Properties.GetProperty("logSequenceNumber").GetString()).IsEqualTo("123456");
    }

    [Test]
    public async Task SumRejectsInvertedNanosecondWindowEvenWhenProjectedTicksMatch()
    {
        const string json = """{"resourceMetrics":[{"scopeMetrics":[{"metrics":[{"name":"requests","sum":{"aggregationTemporality":2,"isMonotonic":true,"dataPoints":[{"startTimeUnixNano":"1700000000000000099","timeUnixNano":"1700000000000000001","asInt":"1"}]}}]}]}]}""";
        await Assert.That(async () => { await Parse(new OtlpJsonSource(), json); }).Throws<InvalidDataException>();
    }

    [Test]
    public async Task UnsupportedMetricKindsFailExplicitly()
    {
        const string json = """{"resourceMetrics":[{"scopeMetrics":[{"metrics":[{"name":"duration","histogram":{"dataPoints":[]}}]}]}]}""";
        await Assert.That(async () => { await Parse(new OtlpJsonSource(), json); }).Throws<InvalidDataException>();
    }

    private static object? ReverseObjectKeys(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Reverse().ToDictionary(x => x.Name, x => ReverseObjectKeys(x.Value)),
        JsonValueKind.Array => value.EnumerateArray().Select(ReverseObjectKeys).ToArray(),
        _ => value.Clone()
    };

    private static string Attribute(SourceRecord record, string name) => record.Properties.GetProperty("attributes").EnumerateArray()
        .Single(x => x.GetProperty("name").GetString() == name).GetProperty("valueJson").GetString()!;
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "NativeFixtures", "Telemetry", name);
    private static Task<List<SourceRecord>> Fixture(IRecordSource source, string name) => Read(source, new(FixturePath(name), "collector-one", "capture-one"));
    private static async Task<List<SourceRecord>> Parse(IRecordSource source, string json)
    {
        var path = Path.GetTempFileName();
        try { await File.WriteAllTextAsync(path, json); return await Read(source, new(path, "collector-one", "capture-one")); }
        finally { File.Delete(path); }
    }
    private static async Task<List<SourceRecord>> Read(IRecordSource source, SourceReadContext context, CancellationToken token = default)
    {
        var records = new List<SourceRecord>();
        await foreach (var record in source.ReadAsync(context, token)) records.Add(record);
        return records;
    }
}
