using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.CollectorProtocol;

namespace Heartbeat.Collection.CollectorProtocol.Tests;

public sealed class CollectorCacheCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-cache-compatibility-{Guid.NewGuid():N}");
    private string PathFor(string name = "outbox") => Path.Combine(_root, $"collector-protocol-{name}.json");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-11T00:00:00Z");
    private static readonly CollectorOutputBinding[] Outputs = [new("activity", "activity", new Dictionary<string, string>())];

    public CollectorCacheCompatibilityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void HistoricalSdkV1PreservesFactAndGapInsteadOfTreatingRetiredMetadataAsCorruption()
    {
        var original = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CacheCompatibility", "outbox-v1.json"));
        File.WriteAllText(PathFor(), original);
        var outbox = CollectorProtocolOutbox.Open(_root, 16, Outputs, Now);
        var fact = Assert.Single(outbox.Facts).Fact;
        Assert.Equal(Guid.Parse("01990000-0000-7000-8000-000000000002"), fact.FactId);
        Assert.Equal(7, fact.Revision);
        Assert.False(Assert.IsType<CollectorSegmentFactTime>(fact.Time).IsFinal);
        Assert.Equal("retained", fact.Payload.GetProperty("extra").GetProperty("array")[2].GetString());
        Assert.Null(fact.Kind);
        Assert.Null(fact.CollectorId);
        Assert.Equal("offline_capacity", Assert.Single(outbox.Gaps).Gap.Reason);
        Assert.Equal(original, File.ReadAllText(PathFor() + ".v1.bak"));
        outbox.BeginActivation();
        var reopened = CollectorProtocolOutbox.Open(_root, 16, Outputs, Now);
        Assert.Equal(fact, Assert.Single(reopened.Facts).Fact with { Payload = fact.Payload });
        Assert.Empty(Directory.GetFiles(_root, "*.corrupt-*"));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void SupportedSdkVersionsKeepPendingAndDeadLetterSnapshotsThroughNativeUpgrade(int version)
    {
        var original = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CacheCompatibility", $"outbox-v{version}.json"));
        File.WriteAllText(PathFor(), original);
        var old = JsonNode.Parse(original)!;
        var entry = new JsonObject
        {
            ["FailedAt"] = "2026-08-28T09:00:00Z",
            ["MessageId"] = "01990000-0000-7000-8000-000000000008",
            ["Fact"] = old["State"]!["Facts"]![0]!["Fact"]!.DeepClone(),
            ["Error"] = new JsonObject { ["Code"] = "invalid_fact", ["Message"] = "retained evidence", ["Retryable"] = false }
        };
        var deadLetter = new JsonObject { ["SchemaVersion"] = version,
            ["State"] = new JsonObject { ["Entries"] = new JsonArray(entry) } }.ToJsonString();
        File.WriteAllText(PathFor("dead-letter"), deadLetter);
        var outbox = CollectorProtocolOutbox.Open(_root, 16, Outputs, Now);
        var fact = Assert.Single(outbox.Facts).Fact;
        Assert.Equal(1, outbox.DeadLetterCount);
        Assert.Equal(version == 1 ? null : "custom.activity", fact.Aspect);
        Assert.Equal(version == 1 ? null : Guid.Parse("01990000-0000-7000-8000-000000000007"), fact.CollectorId);
        outbox.Enqueue(new CollectorFact("", Guid.CreateVersion7(), 1, null,
            new CollectorEventFactTime(Now), JsonSerializer.SerializeToElement(new[] { "native" }),
            Guid.CreateVersion7(), new("account", "service", "user"), "state", Kind: "event"));
        var reopened = CollectorProtocolOutbox.Open(_root, 16, Outputs, Now);
        Assert.Equal(2, reopened.Facts.Count);
        Assert.Equal(fact.FactId, reopened.Facts[0].Fact.FactId);
        Assert.Equal(7, reopened.Facts[0].Fact.Revision);
        Assert.True(JsonElement.DeepEquals(fact.Payload, reopened.Facts[0].Fact.Payload));
        Assert.Equal(original, File.ReadAllText(PathFor() + $".v{version}.bak"));
        Assert.Equal(4, JsonNode.Parse(File.ReadAllText(PathFor()))!["SchemaVersion"]!.GetValue<int>());
        var retained = JsonNode.Parse(File.ReadAllText(PathFor("dead-letter")))!["State"]!["Entries"]![0]!;
        Assert.Equal("retained", retained["Fact"]!["Payload"]!["extra"]!["array"]![2]!.GetValue<string>());
        Assert.Equal("retained evidence", retained["Error"]!["Message"]!.GetValue<string>());
        Assert.Equal("offline_capacity", Assert.Single(reopened.Gaps).Gap.Reason);
    }

    [Fact]
    public void FailedLegacyConversionKeepsOriginalUntilCompatibleRecovery()
    {
        var original = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CacheCompatibility", "outbox-v1.json"));
        var unsupported = original.Replace("\"RecordState\":0", "\"RecordState\":1", StringComparison.Ordinal);
        Assert.NotEqual(original, unsupported);
        File.WriteAllText(PathFor(), unsupported);
        Assert.Throws<NotSupportedException>(() => CollectorProtocolOutbox.Open(_root, 16, Outputs, Now));
        Assert.Equal(unsupported, File.ReadAllText(PathFor()));
        Assert.Equal([PathFor()], Directory.GetFiles(_root));
        File.WriteAllText(PathFor(), original);
        Assert.Single(CollectorProtocolOutbox.Open(_root, 16, Outputs, Now).Facts);
    }

    [Fact]
    public void RecognizedHistoricalEnvelopeWithInvalidDeliveryOrderDoesNotInventLoss()
    {
        var original = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CacheCompatibility", "outbox-v3.json"));
        var node = JsonNode.Parse(original)!;
        node["State"]!["DeliveryOrder"] = new JsonArray("01990000-0000-7000-8000-000000000099");
        var invalid = node.ToJsonString();
        File.WriteAllText(PathFor(), invalid);
        Assert.Throws<NotSupportedException>(() => CollectorProtocolOutbox.Open(_root, 16, Outputs, Now));
        Assert.Equal(invalid, File.ReadAllText(PathFor()));
        Assert.Equal([PathFor()], Directory.GetFiles(_root));
    }

    [Fact]
    public void FailedUpgradePublicationPreservesOriginalAndBackupForRetry()
    {
        var original = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CacheCompatibility", "outbox-v1.json"));
        File.WriteAllText(PathFor(), original);
        Assert.Throws<IOException>(() => CollectorProtocolOutbox.Open(_root, 16, Outputs, Now,
            (_, _) => throw new IOException("simulated publication failure")));
        Assert.Equal(original, File.ReadAllText(PathFor()));
        Assert.Equal(original, File.ReadAllText(PathFor() + ".v1.bak"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Assert.Single(CollectorProtocolOutbox.Open(_root, 16, Outputs, Now).Facts);
    }

    [Fact]
    public void DrainingNativeOutboxDoesNotReopenItToOldPackages()
    {
        var outbox = CollectorProtocolOutbox.Open(_root, 16, Outputs, Now);
        outbox.Enqueue(new CollectorFact("", Guid.CreateVersion7(), 1, null,
            new CollectorEventFactTime(Now), JsonSerializer.SerializeToElement(new[] { "retained" }),
            Guid.CreateVersion7(), new("account", "service", "user"), "state", Kind: "event"));
        var delivery = new CollectorDeliveryOwnership().BeginBackground();
        outbox.AcknowledgeFact(Assert.Single(outbox.Facts).MessageId, delivery);
        Assert.Equal(4, JsonNode.Parse(File.ReadAllText(PathFor()))!["SchemaVersion"]!.GetValue<int>());
    }
}
