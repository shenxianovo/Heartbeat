using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.DTOs.Persons;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Server.Tests.Services;

public sealed partial class FactHttpTests
{
    [Fact]
    public async Task BrowserFold_MultipleWindowsSurviveRuntimeRestart_AndReadNativeRevisionsThroughHttp()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-browser-observation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using (var db = CreateDbContext())
            {
                db.Users.Add(new User { Id = "browser-owner", Username = "browser-fixture" });
                await db.SaveChangesAsync();
            }
            await using var application = CreateApplication().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.Configure<AdministrationOptions>(options => options.Subjects = ["browser-owner"])));
            using var http = application.CreateClient();
            http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
            http.DefaultRequestHeaders.Add("X-Test-Owner", "browser-owner");
            var api = new HeartbeatApiClient(http);
            const string query = "/api/v1/users/browser-fixture/facts/segments";
            var package = LocalCollectorPackage.Load(Path.Combine(AppContext.BaseDirectory, "CollectorPackages", "Browser"));
            var installations = new CollectorPackageInstallations(Path.Combine(directory, "packages"));
            installations.Install(package.PackageDirectory);
            var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine);
            var runtimePath = Path.Combine(directory, "runtime.json");
            var observer = Guid.Parse("6a8259d1-5f6a-4b83-b6ba-870178863199");
            List<FactUploadItem> first;
            Guid instanceId;
            using (var runtime = CollectorRuntime.Open(runtimePath, new SegmentIngestService(new SystemClock())))
            {
                var blueprint = package.Manifest.DefaultInstance!;
                instanceId = runtime.CreateInstance(package, subject,
                    new CollectorInstanceSpec(1, blueprint.ConfigVersion, blueprint.Config.Clone()), CollectorRuntime.DefaultInstanceKey).CollectorInstanceId;
                await RunBrowserProducer(runtime, installations, subject, package, directory, "start");
                first = runtime.ReadPendingFacts();
                Assert.Equal(2, first.Count);
                Assert.All(first, item =>
                {
                    Assert.NotNull(item.Observation);
                    Assert.Null(item.Stream);
                    Assert.Null(item.Fact);
                    Assert.Equal(observer, item.Observation.CollectorId);
                    Assert.NotEqual(instanceId, item.Observation.CollectorId);
                    Assert.Equal(1, item.Observation.Revision);
                });
                // Deliberately leave Analytics offline: ExternalHost ACK only transfers custody to Runtime.
            }
            using var restored = CollectorRuntime.Open(runtimePath, new SegmentIngestService(new SystemClock()));
            var pending = restored.ReadPendingFacts();
            Assert.Equal(first.Select(item => item.Observation!.Id), pending.Select(item => item.Observation!.Id));
            var uploaded = await api.UploadFactsAsync(pending);
            Assert.True(uploaded.Success, uploaded.ResponseBody);
            var before = (await http.GetFromJsonAsync<List<FactResponse>>(query))!;
            Assert.Equal(2, before.Count);
            Assert.Equal(2, before.Select(fact => fact.Id).Distinct().Count());
            Assert.Single(before.Select(fact => fact.FoiId).Distinct());
            Assert.All(before, fact =>
            {
                Assert.Contains(first, item => item.Observation!.Id == fact.Id);
                Assert.Null(fact.FactId);
                Assert.Null(fact.StreamId);
                Assert.Equal(observer, fact.CollectorId);
                Assert.Equal("app", fact.Foi!.Kind);
                Assert.Equal("selected-page", fact.Aspect);
                Assert.NotNull(fact.DeviceId);
                Assert.Contains(fact.Relations, relation => relation.Kind == "observed-on");
            });
            Assert.Equal(2, before.Select(fact => fact.Payload.GetProperty("attributes").GetProperty("windowId").GetInt32()).Distinct().Count());
            restored.ConfirmUploadedFacts(first);
            Assert.Empty(restored.ReadPendingFacts());
            await restored.DisposeAsync();
            using var resumed = CollectorRuntime.Open(runtimePath, new SegmentIngestService(new SystemClock()));
            await RunBrowserProducer(resumed, installations, subject, package, directory, "revise");
            // This delayed Analytics ACK must not confirm the newer snapshots now in custody.
            resumed.ConfirmUploadedFacts(first);
            pending = resumed.ReadPendingFacts();
            Assert.Equal(3, pending.Count(item => item.Observation is not null));
            Assert.Single(pending, item => item.Gap is not null);
            uploaded = await api.UploadFactsAsync(pending);
            Assert.True(uploaded.Success, uploaded.ResponseBody);
            resumed.ConfirmUploadedFacts(pending);
            Assert.Empty(resumed.ReadPendingFacts());
            var after = (await http.GetFromJsonAsync<List<FactResponse>>(query))!;
            Assert.Equal(3, after.Count);
            var original = before.Single(fact => fact.Payload.GetProperty("attributes").GetProperty("windowId").GetInt32() == 11);
            var revised = after.Single(fact => fact.Id == original.Id);
            Assert.True(revised.Revision > original.Revision);
            Assert.True(revised.End < original.End);
            Assert.Equal("Revised title", revised.Payload.GetProperty("title").GetString());
            Assert.Equal(original.Start, revised.Start);
            Assert.All(after, fact => { Assert.Equal(observer, fact.CollectorId); Assert.Equal(original.FoiId, fact.FoiId); });
            var filtered = (await http.GetFromJsonAsync<List<FactResponse>>($"{query}?deviceId={original.DeviceId}&appId={original.AppId}"))!;
            Assert.Equal(after.Select(fact => fact.Id).Order(), filtered.Select(fact => fact.Id).Order());

            // Keep the real multi-window snapshots connected to the public maintenance and
            // personal views: catalog maintenance must not invalidate device evidence or replay.
            using var established = await http.PutAsync("/api/v1/me/person", null);
            established.EnsureSuccessStatusCode();
            const string personQuery = "/api/v1/me/person/facts/segments";
            Assert.Empty((await http.GetFromJsonAsync<PersonFactPage>(personQuery))!.Items);
            var device = Assert.Single(Assert.Single(revised.Relations).Members, member => member.Role == "device").Object;
            using var linked = await http.PostAsJsonAsync("/api/v1/me/person/associations",
                new { objectId = device.Id, start = (DateTimeOffset?)null, end = (DateTimeOffset?)null });
            linked.EnsureSuccessStatusCode();
            await SetProductOverride(http, "mac:com.google.Chrome", "browser-integrated-corrected");
            var corrected = (await http.GetFromJsonAsync<List<FactResponse>>(query))!;
            Assert.All(corrected, fact =>
            {
                Assert.Equal("browser-integrated-corrected", fact.Foi!.Key);
                Assert.NotEqual(original.FoiId, fact.FoiId);
                Assert.Equal(fact.FoiId, ProductObject(fact).Id);
                AssertProductFactPreserved(after.Single(prior => prior.Id == fact.Id), fact);
            });
            await RunBrowserProducer(resumed, installations, subject, package, directory, "replay");
            uploaded = await api.UploadFactsAsync(resumed.ReadPendingFacts());
            Assert.True(uploaded.Success, uploaded.ResponseBody);
            // Replay the actual latest producer snapshots at the same revision after maintenance,
            // even if Runtime already confirmed them and has no pending upload left.
            Assert.True((await api.UploadFactsAsync(pending)).Success);
            // A delayed original producer snapshot still carries the pre-maintenance platform
            // reference. Its lower revision cannot undo the corrected product or window result.
            Assert.True((await api.UploadFactsAsync(first)).Success);
            var replayed = (await http.GetFromJsonAsync<List<FactResponse>>(query))!;
            Assert.Equal(3, replayed.Count);
            Assert.All(replayed, fact =>
            {
                var maintained = corrected.Single(prior => prior.Id == fact.Id);
                Assert.Equal(maintained.FoiId, fact.FoiId);
                Assert.Equal(maintained.AppId, fact.AppId);
                AssertProductFactPreserved(maintained, fact);
            });
            var personal = (await http.GetFromJsonAsync<PersonFactPage>(personQuery))!;
            Assert.Equal(replayed.Select(fact => fact.Id).Order(), personal.Items.Select(item => item.Fact.Id).Order());
            Assert.All(personal.Items, item =>
            {
                Assert.Equal("browser-integrated-corrected", item.Fact.Foi!.Key);
                Assert.Equal(original.DeviceId, item.Fact.DeviceId);
            });
            var correctedFilter = (await http.GetFromJsonAsync<List<FactResponse>>(
                $"{query}?deviceId={original.DeviceId}&appId={replayed[0].AppId}"))!;
            Assert.Equal(replayed.Select(fact => fact.Id).Order(), correctedFilter.Select(fact => fact.Id).Order());
            Assert.Empty((await http.GetFromJsonAsync<List<FactResponse>>($"{query}?appId={original.AppId}"))!);

            await RunBrowserProducer(resumed, installations, subject, package, directory, "legacy-start");
            var legacy = Assert.Single(resumed.ReadPendingFacts(), item => item.Fact is not null);
            uploaded = await api.UploadFactsAsync(resumed.ReadPendingFacts());
            Assert.True(uploaded.Success, uploaded.ResponseBody);
            var historical = (await http.GetFromJsonAsync<List<FactResponse>>(query))!.Single(fact => fact.FactId == legacy.Fact!.FactId);
            Assert.NotEqual(historical.Id, historical.FactId);
            resumed.ConfirmUploadedFacts(resumed.ReadPendingFacts());
            await resumed.DisposeAsync();
            using var legacyRestored = CollectorRuntime.Open(runtimePath, new SegmentIngestService(new SystemClock()));
            await RunBrowserProducer(legacyRestored, installations, subject, package, directory, "recover");
            var recovered = Assert.Single(legacyRestored.ReadPendingFacts());
            Assert.Null(recovered.Observation);
            Assert.Equal(legacy.Fact!.FactId, recovered.Fact!.FactId);
            Assert.Equal(legacy.Fact.StreamId, recovered.Fact.StreamId);
            Assert.Equal(legacy.Fact.Revision + 1, recovered.Fact.Revision);
            Assert.Equal(legacy.Fact.End, recovered.Fact.End);
            Assert.True(JsonElement.DeepEquals(legacy.Fact.Payload!.Value, recovered.Fact.Payload!.Value));
            uploaded = await api.UploadFactsAsync(legacyRestored.ReadPendingFacts());
            Assert.True(uploaded.Success, uploaded.ResponseBody);
            legacyRestored.ConfirmUploadedFacts(legacyRestored.ReadPendingFacts());
            Assert.Empty(legacyRestored.ReadPendingFacts());
            var historicalAfter = (await http.GetFromJsonAsync<List<FactResponse>>(query))!.Single(fact => fact.Id == historical.Id);
            Assert.Equal(historical.FactId, historicalAfter.FactId);
            Assert.Equal(historical.End, historicalAfter.End);
            Assert.Equal(historical.Revision + 1, historicalAfter.Revision);
            Assert.True(JsonElement.DeepEquals(historical.Payload, historicalAfter.Payload));

        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task RunBrowserProducer(CollectorRuntime runtime, CollectorPackageInstallations installations,
        SubjectReference subject, LocalCollectorPackage package, string directory, string phase)
    {
        await using var handler = new ExternalHostCollectorProtocolHandler(runtime, new BrowserDeclarations(), installations, () => subject);
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var host = builder.Build();
        host.MapFallback(async context =>
        {
            var response = await handler.HandleAsync(context.Request.Method, context.Request.Path.Value, context.Request.Body, context.RequestAborted);
            context.Response.StatusCode = response?.StatusCode ?? 404;
            if (response is not null)
            {
                context.Response.ContentType = response.IsJson ? "application/json" : "text/plain";
                await context.Response.WriteAsync(response.Body);
            }
        });
        await host.StartAsync();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Heartbeat.slnx")))
            root = root.Parent ?? throw new InvalidOperationException("Browser integration requires the repository checkout.");
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(root.FullName, "collection/collectors/Heartbeat.Collector.Browser/tests/fixtures/run-observation-producer.mjs"));
        start.ArgumentList.Add(new Uri(host.Urls.Single()).Port.ToString());
        start.ArgumentList.Add(Path.Combine(package.PackageDirectory, "browser-extension", "collector-artifact-ref.json"));
        start.ArgumentList.Add(Path.Combine(directory, "browser-checkpoint.json"));
        start.ArgumentList.Add(phase);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.True(process.ExitCode == 0, $"{await output}\n{await error}");
        await host.StopAsync();
    }

    private sealed class BrowserDeclarations : ICollectorDeclarationStore
    {
        public IReadOnlyDictionary<string, CollectorRegistration> Snapshot { get; } = new Dictionary<string, CollectorRegistration>();
        public void StoreVerifiedPackageDeclaration(string source, string declarationJson, int version) { }
    }
}
