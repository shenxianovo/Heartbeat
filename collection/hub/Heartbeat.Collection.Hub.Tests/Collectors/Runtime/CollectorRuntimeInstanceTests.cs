using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Runtime;

public class CollectorRuntimeInstanceTests
{
    private static string ReferencePackagePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "ReferenceCollectorPackage");

    [Fact]
    public void Open_RejectsDrainBudgetThatCannotBeScheduledByTimeProvider()
    {
        using var stateDirectory = TemporaryDirectory.Create();
        var statePath = Path.Combine(stateDirectory.Path, "collector-runtime.json");

        Assert.Throws<ArgumentOutOfRangeException>(() => CollectorRuntime.Open(
            statePath,
            new RecordingSegmentSink(),
            new CollectorRuntimeOptions { InProcessDrainGracePeriod = TimeSpan.MaxValue }));
    }

    [Fact]
    public void CreateInstance_RuntimeReopensWithSamePackageAndSubjectBinding()
    {
        using var stateDirectory = TemporaryDirectory.Create();
        var statePath = Path.Combine(stateDirectory.Path, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var subject = new SubjectReference(
            Guid.Parse("0198d5df-5df3-70a1-937d-68a7d64623e2"),
            SubjectKind.Machine);
        using var configDocument = JsonDocument.Parse("{}");
        var spec = new CollectorInstanceSpec(1, 1, configDocument.RootElement.Clone());
        var expectedInstanceId = Guid.Parse("0198d5e0-5d15-73d8-a6d8-84a50ddf855f");

        CollectorInstance created;
        using (var runtime = CollectorRuntime.Open(
                   statePath,
                   new RecordingSegmentSink(),
                   new CollectorRuntimeOptions { IdGenerator = () => expectedInstanceId }))
        {
            created = runtime.CreateInstance(package, subject, spec);
        }

        using var reopened = CollectorRuntime.Open(statePath, new RecordingSegmentSink());
        var restored = reopened.GetInstance(created.CollectorInstanceId);

        Assert.Equal(expectedInstanceId, created.CollectorInstanceId);
        Assert.Equal(package.Manifest.PackageId, restored.PackageId);
        Assert.Equal(subject, restored.Subject);
        Assert.Equal(spec.SpecRevision, restored.Spec.SpecRevision);
    }

    [Fact]
    public void Open_LegacyRuntimeStateNames_LoadsAndRewritesCanonicalNamesOnNextSave()
    {
        using var stateDirectory = TemporaryDirectory.Create();
        var statePath = Path.Combine(stateDirectory.Path, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var configDocument = JsonDocument.Parse("{}");
        Guid collectorInstanceId;
        using (var runtime = CollectorRuntime.Open(statePath, new RecordingSegmentSink()))
        {
            collectorInstanceId = runtime.CreateInstance(
                package,
                new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
                new CollectorInstanceSpec(1, 1, configDocument.RootElement.Clone()))
                .CollectorInstanceId;
        }
        // Pinned against the current schema version rather than a literal, so adding a later
        // migration cannot silently turn this into a test that never builds a legacy document.
        var legacyJson = File.ReadAllText(statePath)
            .Replace(
                $"\"schemaVersion\": {JsonCollectorRuntimeStore.CurrentSchemaVersion}",
                "\"schemaVersion\": 1",
                StringComparison.Ordinal)
            .Replace("\"configVersion\"", "\"configSchemaVersion\"", StringComparison.Ordinal)
            .Replace("\"activationAttemptTombstones\"", "\"helloAttempts\"", StringComparison.Ordinal);
        File.WriteAllText(statePath, legacyJson);

        using (var reopened = CollectorRuntime.Open(statePath, new RecordingSegmentSink()))
        {
            Assert.Equal(1, reopened.GetInstance(collectorInstanceId).Spec.ConfigVersion);
            reopened.UpdateInstanceSpec(
                collectorInstanceId,
                2,
                configDocument.RootElement.Clone());
        }

        var canonicalJson = File.ReadAllText(statePath);
        Assert.Contains(
            $"\"schemaVersion\": {JsonCollectorRuntimeStore.CurrentSchemaVersion}",
            canonicalJson,
            StringComparison.Ordinal);
        Assert.Contains("\"configVersion\"", canonicalJson, StringComparison.Ordinal);
        Assert.Contains("\"activationAttemptTombstones\"", canonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"configSchemaVersion\"", canonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"helloAttempts\"", canonicalJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// A state file written before the Collector Package update record existed is still a complete
    /// state file: the record is optional, so an older Hub's document opens unchanged and simply
    /// carries no update facts yet.
    /// </summary>
    [Fact]
    public void Open_RuntimeStateWithoutPackageUpdateRecord_LoadsAndAdoptsTheCurrentSchemaVersion()
    {
        using var stateDirectory = TemporaryDirectory.Create();
        var statePath = Path.Combine(stateDirectory.Path, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var configDocument = JsonDocument.Parse("{}");
        Guid collectorInstanceId;
        using (var runtime = CollectorRuntime.Open(statePath, new RecordingSegmentSink()))
        {
            collectorInstanceId = runtime.CreateInstance(
                package,
                new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
                new CollectorInstanceSpec(1, 1, configDocument.RootElement.Clone()))
                .CollectorInstanceId;
        }
        File.WriteAllText(
            statePath,
            File.ReadAllText(statePath).Replace(
                $"\"schemaVersion\": {JsonCollectorRuntimeStore.CurrentSchemaVersion}",
                "\"schemaVersion\": 2",
                StringComparison.Ordinal));

        using (var reopened = CollectorRuntime.Open(statePath, new RecordingSegmentSink()))
        {
            var status = reopened.GetPackageUpdateStatus(collectorInstanceId);
            Assert.Null(status.InstalledCandidate);
            Assert.Null(status.ApprovedCandidate);
            Assert.Null(status.RegistryCurrent);
            Assert.Null(status.LastFailure);
            reopened.UpdateInstanceSpec(collectorInstanceId, 2, configDocument.RootElement.Clone());
        }

        Assert.Contains(
            $"\"schemaVersion\": {JsonCollectorRuntimeStore.CurrentSchemaVersion}",
            File.ReadAllText(statePath),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Open_ObsoletePackageFingerprintCatalog_LoadsAndRemovesItOnNextSave()
    {
        using var stateDirectory = TemporaryDirectory.Create();
        var statePath = Path.Combine(stateDirectory.Path, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var configDocument = JsonDocument.Parse("{}");
        Guid collectorInstanceId;
        using (var runtime = CollectorRuntime.Open(statePath, new RecordingSegmentSink()))
        {
            collectorInstanceId = runtime.CreateInstance(
                package,
                new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
                new CollectorInstanceSpec(1, 1, configDocument.RootElement.Clone()))
                .CollectorInstanceId;
        }
        var root = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        root["instances"]![0]!["packageFingerprints"] = new JsonObject
        {
            [package.Manifest.Version] = package.PackageContentHash
        };
        File.WriteAllText(statePath, root.ToJsonString());

        using (var reopened = CollectorRuntime.Open(statePath, new RecordingSegmentSink()))
        {
            reopened.UpdateInstanceSpec(
                collectorInstanceId,
                2,
                configDocument.RootElement.Clone());
        }

        Assert.DoesNotContain("\"packageFingerprints\"", File.ReadAllText(statePath), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateInstance_DifferentSubjectAlwaysGetsNewStableIdentity()
    {
        using var stateDirectory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(stateDirectory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var spec = new CollectorInstanceSpec(1, 1, config.RootElement.Clone());
        var first = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            spec);
        var second = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            spec);

        Assert.NotEqual(first.CollectorInstanceId, second.CollectorInstanceId);
        Assert.NotEqual(first.Subject, second.Subject);
        Assert.Equal(first.PackageId, second.PackageId);
    }

    [Fact]
    public void FindInstances_AllowsMultipleInstancesForTheSamePackageAndSubject()
    {
        using var stateDirectory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var subject = new SubjectReference(
            Guid.Parse("0198d5df-5df3-70a1-937d-68a7d64623e2"),
            SubjectKind.Machine);
        using var config = JsonDocument.Parse("{}");
        var spec = new CollectorInstanceSpec(1, 1, config.RootElement.Clone());
        using var runtime = CollectorRuntime.Open(
            Path.Combine(stateDirectory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        var first = runtime.CreateInstance(package, subject, spec);
        var second = runtime.CreateInstance(package, subject, spec);

        var matches = runtime.FindInstances(package.Manifest.PackageId, subject);

        Assert.Equal([first.CollectorInstanceId, second.CollectorInstanceId],
            matches.Select(instance => instance.CollectorInstanceId));
    }

    [Fact]
    public void InstanceKey_IsUniqueWithinPackageAndSubjectAndSurvivesRestart()
    {
        using var stateDirectory = TemporaryDirectory.Create();
        var statePath = Path.Combine(stateDirectory.Path, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine);
        using var config = JsonDocument.Parse("{}");
        var spec = new CollectorInstanceSpec(1, 1, config.RootElement.Clone());
        Guid expectedId;

        using (var runtime = CollectorRuntime.Open(statePath, new RecordingSegmentSink()))
        {
            var chrome = runtime.CreateInstance(package, subject, spec, "app/chrome");
            expectedId = chrome.CollectorInstanceId;

            Assert.Equal("app/chrome", chrome.InstanceKey);
            Assert.Equal(expectedId, runtime.FindInstance(
                package.Manifest.PackageId,
                subject,
                "app/chrome")?.CollectorInstanceId);
            Assert.Throws<InvalidOperationException>(() =>
                runtime.CreateInstance(package, subject, spec, "app/chrome"));

            var edge = runtime.CreateInstance(package, subject, spec, "app/edge");
            Assert.NotEqual(expectedId, edge.CollectorInstanceId);
        }

        using var reopened = CollectorRuntime.Open(statePath, new RecordingSegmentSink());
        var restored = reopened.FindInstance(package.Manifest.PackageId, subject, "app/chrome");
        Assert.NotNull(restored);
        Assert.Equal(expectedId, restored.CollectorInstanceId);
        Assert.Equal("app/chrome", restored.InstanceKey);
    }

    [Fact]
    public void InstanceKey_SameValueIsIndependentAcrossSubjects()
    {
        using var stateDirectory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(stateDirectory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var spec = new CollectorInstanceSpec(1, 1, config.RootElement.Clone());

        var first = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            spec,
            "app/chrome");
        var second = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            spec,
            "app/chrome");

        Assert.NotEqual(first.CollectorInstanceId, second.CollectorInstanceId);
    }

    [Fact]
    public void Open_SameStateFileAlreadyOwned_RejectsConcurrentRuntime()
    {
        using var stateDirectory = TemporaryDirectory.Create();
        var statePath = Path.Combine(stateDirectory.Path, "collector-runtime.json");
        using var owner = CollectorRuntime.Open(statePath, new RecordingSegmentSink());

        var error = Assert.Throws<CollectorRuntimeStateException>(() =>
            CollectorRuntime.Open(statePath, new RecordingSegmentSink()));

        Assert.Contains("already has an owner", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_StateCollectionContainsNullEntry_ReportsStateError()
    {
        using var stateDirectory = TemporaryDirectory.Create();
        var statePath = Path.Combine(stateDirectory.Path, "collector-runtime.json");
        File.WriteAllText(
            statePath,
            """{"schemaVersion":1,"instances":[null],"streams":[],"facts":[],"gaps":[]}""");

        var error = Assert.Throws<CollectorRuntimeStateException>(() =>
            CollectorRuntime.Open(statePath, new RecordingSegmentSink()));

        Assert.Contains("Unable to load", error.Message, StringComparison.Ordinal);
        Assert.IsType<JsonException>(error.InnerException);
    }

    private sealed class RecordingSegmentSink : ISegmentSink
    {
        public List<ActivitySegmentItem> Segments { get; } = [];

        public void Push(List<ActivitySegmentItem> snapshots) => Segments.AddRange(snapshots);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"heartbeat-collector-runtime-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
