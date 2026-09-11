using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Runtime;

public sealed class CollectorRuntimeCacheCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-runtime-compatibility-{Guid.NewGuid():N}");
    private string StatePath => Path.Combine(_root, "runtime.json");
    private static Guid Id(int value) => Guid.Parse($"01990000-0000-7000-8000-{value:D12}");
    public CollectorRuntimeCacheCompatibilityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void HistoricalRuntimePreservesDeliveryIdentityWholeResultAndPackageRecovery(int version)
    {
        var original = Fixture(version);
        File.WriteAllText(StatePath, original);
        using (var runtime = CollectorRuntime.Open(StatePath, new Sink()))
        {
            var instance = runtime.GetInstance(Id(1));
            Assert.Equal("0.9.0", instance.LastKnownGoodPackage!.PackageVersion);
            Assert.Equal(2, instance.Spec.ConfigVersion);
            Assert.True(instance.Spec.Config.GetProperty("offline").GetBoolean());
            var pending = runtime.ReadPendingFacts();
            Assert.Equal(2, pending.Count);
            var fact = Assert.Single(pending, item => item.Fact is not null).Fact!;
            Assert.Equal(Id(5), fact.FactId);
            Assert.Equal(Id(4), fact.StreamId);
            Assert.Equal(2, fact.Revision);
            Assert.Equal(DateTimeOffset.Parse("2026-08-28T08:01:00Z"), fact.OccurredAt);
            Assert.Equal(1.5, fact.Payload!.Value.GetProperty("opaque")[2].GetProperty("n").GetDouble());
            Assert.Equal("offline_capacity", Assert.Single(pending, item => item.Gap is not null).Gap!.Reason);
            runtime.ConfirmUploadedFacts(pending);
        }
        Assert.Equal(original, File.ReadAllText(StatePath + $".v{version}.bak"));
        var saved = JsonNode.Parse(File.ReadAllText(StatePath))!;
        Assert.Equal(9, saved["schemaVersion"]!.GetValue<int>());
        Assert.Equal(2, saved["facts"]!.AsArray().Count);
        var ongoing = saved["facts"]![0]!;
        Assert.Equal(7, ongoing["revision"]!.GetValue<int>());
        Assert.False(ongoing["isFinal"]!.GetValue<bool>());
        Assert.True(ongoing["delivered"]!.GetValue<bool>());
        Assert.Equal("2026-08-28T08:02:00+00:00", ongoing["end"]!.GetValue<string>());
        Assert.Equal("full", ongoing["payload"]!["extra"]!["array"]![3]!["retained"]!.GetValue<string>());
        using var reopened = CollectorRuntime.Open(StatePath, new Sink());
        Assert.Empty(reopened.ReadPendingFacts());
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(StatePath))!["facts"]!.AsArray().Count);
    }

    [Fact]
    public void FailedRuntimeUpgradePublicationPreservesOriginalAndRetriesWithSameFactIdentity()
    {
        var original = Fixture(2);
        File.WriteAllText(StatePath, original);
        Assert.Throws<CollectorRuntimeStateException>(() => CollectorRuntime.Open(StatePath, new Sink(),
            new CollectorRuntimeOptions { BeforeStatePrepare = () => throw new IOException("simulated full disk") }));
        Assert.Equal(original, File.ReadAllText(StatePath));
        Assert.Equal(original, File.ReadAllText(StatePath + ".v2.bak"));
        using var reopened = CollectorRuntime.Open(StatePath, new Sink());
        Assert.Equal(Id(5), Assert.Single(reopened.ReadPendingFacts(), item => item.Fact is not null).Fact!.FactId);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void UnconvertibleHistoricalRecordCannotResurrectAndKeepsOriginal()
    {
        var original = Fixture(2).Replace("\"recordState\": \"present\"", "\"recordState\": \"retracted\"", StringComparison.Ordinal);
        File.WriteAllText(StatePath, original);
        Assert.Throws<CollectorRuntimeStateException>(() => CollectorRuntime.Open(StatePath, new Sink()));
        Assert.Equal(original, File.ReadAllText(StatePath));
    }

    private static string Fixture(int version) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "CacheCompatibility", $"runtime-v{version}.json"));
    private sealed class Sink : ISegmentSink
    {
        public void Push(List<ActivitySegmentItem> snapshots) { }
    }
}
