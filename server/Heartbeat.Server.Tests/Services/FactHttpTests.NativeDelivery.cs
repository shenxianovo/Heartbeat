using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Entities;

namespace Heartbeat.Server.Tests.Services;

public sealed partial class FactHttpTests
{
    [Theory]
    [InlineData("segment")]
    [InlineData("event")]
    public async Task IndependentCollectorProtocol_RestartHttpReadAndExactAckPreserveObservation(string kind)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-native-delivery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // Reuse the shipped in-process package and HTTP fixture; only its output declaration is empty.
            var packagePath = Path.Combine(directory, "package");
            foreach (var file in Directory.EnumerateFiles(SystemCollectorPackage.Path, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(packagePath, Path.GetRelativePath(SystemCollectorPackage.Path, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            var manifestPath = Path.Combine(packagePath, "collector-manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            manifest["outputs"] = new JsonArray();
            manifest.AsObject().Remove("observationDeclaration");
            manifest["supportedCapabilities"]!["facts.observation"] = new JsonArray(2);
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            var package = LocalCollectorPackage.Load(packagePath);
            var path = Path.Combine(directory, "runtime.json");
            Guid instanceId;
            FactSubmission original;
            var start = DateTimeOffset.UtcNow.AddMinutes(-2);
            using (var runtime = CollectorRuntime.Open(path, new UnusedProjection()))
            {
                var instance = runtime.CreateInstance(package, new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { })));
                instanceId = instance.CollectorInstanceId;
                Assert.Equal(Guid.Empty, instance.Subject.SubjectId);
                await using var activation = await runtime.ActivateInProcessAsync(instanceId, package, new PayloadCollector(package));
                Assert.Empty(activation.Streams);
                original = new FactSubmission(Guid.Empty, Guid.NewGuid(), 1, start,
                    kind == "segment" ? new SegmentFactTime(start, start.AddMinutes(1), false) : new EventFactTime(start),
                    JsonSerializer.SerializeToElement(new { unknown = new { values = new[] { 7, 19 } }, title = "original" }),
                    Guid.NewGuid(), new ObservationObjectReference("account", "test-service", "account-17"), "unknown-observation", [], kind);
                foreach (var invalid in new[]
                {
                    original with { CollectorId = null }, original with { Foi = null }, original with { Aspect = null },
                    original with { Payload = JsonSerializer.SerializeToElement<object?>(null) },
                    original with { Foi = new ObservationObjectReference("machine", "invalid-scope", "machine-1") },
                    original with { Relations = [new FactRelationSnapshot("unknown", [])] }
                })
                {
                    var rejected = await activation.PublishAsync(Guid.CreateVersion7(), [invalid]);
                    Assert.False(Assert.Single(rejected.Results).IsAcknowledged);
                    Assert.Empty(runtime.ReadPendingFacts());
                }
                var committed = await activation.PublishAsync(Guid.CreateVersion7(), [original]);
                Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(committed.Results).Status);
                await activation.StopAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RemoveInstanceAsync(instanceId).AsTask());
            }
            await using (var db = CreateDbContext())
            {
                db.Users.Add(new User { Id = "owner", Username = "alice" });
                await db.SaveChangesAsync();
            }
            await using var app = CreateApplication();
            using var http = app.CreateClient();
            http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
            var api = new HeartbeatApiClient(http);
            using (var restarted = CollectorRuntime.Open(path, new UnusedProjection()))
            {
                var first = restarted.ReadPendingFacts();
                var item = Assert.Single(first);
                Assert.Null(item.Stream);
                Assert.Null(item.Fact);
                Assert.Equal(original.FactId, item.Observation!.Id);
                Assert.Equal(kind == "segment" ? false : (bool?)null, item.IsFinal);
                await using var activation = await restarted.ActivateInProcessAsync(instanceId, package, new PayloadCollector(package));
                var latest = original with
                {
                    Revision = 2,
                    Time = kind == "segment" ? new SegmentFactTime(start, start.AddSeconds(12), true) : original.Time,
                    Payload = JsonSerializer.SerializeToElement(new { unknown = new { values = new[] { 7, 19, 31 } }, title = "latest" })
                };
                Assert.Equal(FactDeliveryStatus.Committed,
                    Assert.Single((await activation.PublishAsync(Guid.CreateVersion7(), [latest])).Results).Status);
                Assert.True((await api.UploadFactsAsync(first)).Success);
                restarted.ConfirmUploadedFacts(first);
                Assert.Equal(2, Assert.Single(restarted.ReadPendingFacts()).Observation!.Revision);

                var pending = restarted.ReadPendingFacts();
                var sent = Assert.Single(pending);
                Assert.True((await api.UploadFactsAsync(pending)).Success);
                // A caller cannot confirm a changed same-version snapshot or discard local terminal custody.
                var altered = sent.Observation!;
                altered.Source = "different";
                restarted.ConfirmUploadedFacts([sent]);
                Assert.Single(restarted.ReadPendingFacts());
                altered.Source = null;
                if (kind == "segment")
                {
                    restarted.ConfirmUploadedFacts([sent with { IsFinal = false }]);
                    Assert.Single(restarted.ReadPendingFacts());
                }
                var read = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/{kind}s"))!);
                Assert.Equal(original.FactId, read.Id);
                Assert.Null(read.StreamId);
                Assert.Null(read.FactId);
                Assert.Null(read.Source);
                Assert.Equal(2, read.Revision);
                Assert.Equal(original.CollectorId, read.ObserverId);
                Assert.Equal(kind, read.Kind);
                Assert.True(JsonElement.DeepEquals(latest.Payload, read.Result));
                Assert.Empty(read.Relations);
                Assert.Equal(FactDeliveryStatus.Superseded,
                    Assert.Single((await activation.PublishAsync(Guid.CreateVersion7(), [original])).Results).Status);
                Assert.Equal(FactDeliveryStatus.Duplicate,
                    Assert.Single((await activation.PublishAsync(Guid.CreateVersion7(), [latest])).Results).Status);
                Assert.Equal(FactDeliveryStatus.Rejected,
                    Assert.Single((await activation.PublishAsync(Guid.CreateVersion7(), [latest with { Payload = original.Payload }])).Results).Status);
                Assert.Equal(FactDeliveryStatus.Rejected,
                    Assert.Single((await activation.PublishAsync(Guid.CreateVersion7(), [latest with { Revision = 3, Aspect = "changed" }])).Results).Status);
                restarted.ConfirmUploadedFacts(restarted.ReadPendingFacts());
            }
            using var confirmed = CollectorRuntime.Open(path, new UnusedProjection());
            Assert.Empty(confirmed.ReadPendingFacts());
            Assert.Empty(JsonNode.Parse(File.ReadAllText(path))!["streams"]!.AsArray());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
