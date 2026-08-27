using System.Text.Json;
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
        var legacyJson = File.ReadAllText(statePath)
            .Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1", StringComparison.Ordinal)
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
        Assert.Contains("\"schemaVersion\": 2", canonicalJson, StringComparison.Ordinal);
        Assert.Contains("\"configVersion\"", canonicalJson, StringComparison.Ordinal);
        Assert.Contains("\"activationAttemptTombstones\"", canonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"configSchemaVersion\"", canonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"helloAttempts\"", canonicalJson, StringComparison.Ordinal);
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
