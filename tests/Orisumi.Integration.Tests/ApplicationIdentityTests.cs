using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using Orisumi.Api;
using Orisumi.Application.Entities;
using Orisumi.Observer.Runtime.Store;
using Orisumi.Observer.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Orisumi.Core;
using Orisumi.Observers.ApplicationIdentity;

namespace Orisumi.Integration.Tests;

public sealed class ApplicationIdentityTests
{
    private static CancellationToken Token => TestContext.Current!.Execution.CancellationToken;

    [Test, Property("caseId", "application-identity")]
    public async Task RecognizesOnceAcrossDevicesAndPlatformsAndPreservesEvidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var a = await fixture.ApplicationAsync("com.tencent.qq", "/Applications/QQ", "QQ");
        var b = await fixture.ApplicationAsync("com.tencent.qq", "/Users/test/QQ", "QQ");
        fixture.Model.Decide = _ => new("new", Name: "QQ");
        await fixture.RunAsync();
        var first = (await fixture.ProductsAsync()).Single();
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(1);
        await Assert.That(fixture.Model.Inputs[0].Applications.Length).IsEqualTo(2);
        await Assert.That(fixture.Model.Inputs[0].Names.Single()).IsEqualTo("QQ");
        await Assert.That(fixture.Model.Inputs[0].Platform).IsEqualTo("macos");
        await fixture.RunAsync();
        await fixture.ApplicationAsync("com.tencent.qq", "/Other/QQ", "QQ");
        await fixture.RunAsync();
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(1);
        var android = await fixture.ApplicationAsync("com.tencent.mobileqq", "/android/qq", "QQ", "android.package-name");
        fixture.Model.Decide = _ => new("existing", first.EntityId.Value);
        await fixture.RunAsync();
        var updated = (await fixture.ProductsAsync()).Single();
        await Assert.That(updated.EntityId).IsEqualTo(first.EntityId);
        await Assert.That(IdentityData.Identifiers(updated).Length).IsEqualTo(2);
        var observations = await fixture.ListAsync(category: "observation");
        var latest = observations.Single(o => ((Observation)o.Content).ContentVersionId == updated.VersionId);
        var observation = (Observation)latest.Content;
        await Assert.That(observation.StartAt is null && observation.EndAt is null && observation.TimeZone is null).IsTrue();
        var inputs = (InputSet)(await fixture.ReadVersionAsync(observation.InputSetVersionId!.Value)).Content;
        await Assert.That(inputs.Members.Contains(android.VersionId) && inputs.Members.Contains(first.VersionId)).IsTrue();
        var oldObservation = observations.Single(o => ((Observation)o.Content).ContentVersionId == first.VersionId);
        var oldInputs = (InputSet)(await fixture.ReadVersionAsync(((Observation)oldObservation.Content).InputSetVersionId!.Value)).Content;
        await Assert.That(oldInputs.Members.Contains(a.VersionId) && oldInputs.Members.Contains(b.VersionId)).IsTrue();
        await Assert.That(IdentityData.Identifiers(await fixture.ReadVersionAsync(first.VersionId)).Length).IsEqualTo(1);
        await fixture.ApplicationAsync("unrelated.qq", "/Different/QQ", "QQ");
        fixture.Model.Decide = _ => new("new", Name: "QQ");
        await fixture.RunAsync();
        await Assert.That((await fixture.ProductsAsync()).Count).IsEqualTo(2);
        await Assert.That(fixture.Model.Inputs.Last().Products.Single().EntityId).IsEqualTo(first.EntityId.Value);
    }

    [Test, Property("caseId", "application-identity")]
    public async Task UnknownSurvivesRestartAndRetriesOnlyOnNewNamesOrApplications()
    {
        await using var fixture = await Fixture.CreateAsync();
        var app = await fixture.ApplicationAsync("unknown.app", "/unknown", "Unknown");
        fixture.Model.Decide = _ => new("unknown");
        await fixture.RunAsync();
        await fixture.RunAsync();
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(1);
        await fixture.PeriodAsync(app, "New name");
        await fixture.RunAsync();
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(2);
        await fixture.ApplicationAsync("unknown.app", "/second", "Unknown");
        await fixture.RunAsync();
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(3);
        await Assert.That((await fixture.ProductsAsync()).Count).IsEqualTo(0);
    }

    [Test, Property("caseId", "application-identity")]
    public async Task ModelFailuresAndInvalidTargetsRetryWithoutCreatingProducts()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ApplicationAsync("unknown.app", "/unknown", "Unknown");
        fixture.Model.Decide = _ => throw new TaskCanceledException("timeout");
        await fixture.RunAsync();
        fixture.Model.Decide = _ => new("existing", Guid.CreateVersion7());
        await fixture.RunAsync();
        await Assert.That((await fixture.ProductsAsync()).Count).IsEqualTo(0);
        fixture.Model.Decide = _ => new("new", Name: "Known");
        await fixture.RunAsync();
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(3);
        await Assert.That((await fixture.ProductsAsync()).Count).IsEqualTo(1);
    }

    [Test, Property("caseId", "application-identity")]
    public async Task MissingKeyDoesNotWriteAndModelChangesVersionTheSameObserver()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ApplicationAsync("com.tencent.qq", "/QQ", "QQ");
        fixture.Model.IsConfigured = false;
        await fixture.RunAsync();
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(0);
        await Assert.That((await fixture.ListAsync("application-identity-observers")).Count).IsEqualTo(0);
        await Assert.That(fixture.Diagnostics.Count).IsEqualTo(1);
        fixture.Model.IsConfigured = true;
        fixture.Model.Decide = _ => new("new", Name: "QQ");
        await fixture.RunAsync();
        var before = (await fixture.ListAsync("application-identity-observers")).Single();
        await fixture.RunAsync("another-model");
        var after = (await fixture.ListAsync("application-identity-observers")).Single();
        await Assert.That(after.EntityId).IsEqualTo(before.EntityId);
        await Assert.That(after.PreviousVersionId).IsEqualTo(before.VersionId);
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(1);
    }

    [Test, Property("caseId", "application-identity")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProcessExitDuringDeliveryReplaysTheSameBatchBeforeCheckingTheModel(bool saved)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ApplicationAsync("com.tencent.qq", "/QQ", "QQ");
        fixture.Model.Decide = _ => new("new", Name: "QQ");
        fixture.Model.Before = _ => { fixture.Session.Crash = saved ? "after" : "before"; return Task.CompletedTask; };
        await Assert.That(async () => await fixture.RunAsync()).Throws<SimulatedProcessExit>();
        var pending = Path.Combine(fixture.Directory, "pending.json");
        await Assert.That(File.Exists(pending)).IsTrue();
        var payloads = fixture.Store.Execute(local => local.Versions().Select(item => VersionJson.Submission(item.Version)).ToArray());
        fixture.Session.Crash = null; fixture.Model.Before = null; fixture.Model.IsConfigured = false;
        await fixture.RunAsync();
        await Assert.That(File.Exists(pending)).IsFalse();
        await Assert.That((await fixture.ProductsAsync()).Count).IsEqualTo(1);
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(1);
        foreach (var expected in payloads)
        {
            var actual = await fixture.ReadVersionAsync(new(expected.GetProperty("versionId").GetGuid()));
            await Assert.That(JsonElement.DeepEquals(expected, VersionJson.Submission(actual.Submission))).IsTrue();
        }
    }

    [Test, Property("caseId", "application-identity")]
    public async Task StaleProductIsRereadAndRemovedIdentifiersAreRecognizedAgain()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ApplicationAsync("com.tencent.qq", "/QQ", "QQ");
        fixture.Model.Decide = _ => new("new", Name: "QQ");
        await fixture.RunAsync();
        var product = (await fixture.ProductsAsync()).Single();
        await fixture.ApplicationAsync("com.tencent.mobileqq", "/android", "QQ", "android.package-name");
        fixture.Model.Decide = _ => new("existing", product.EntityId.Value);
        fixture.Model.Before = async _ =>
        {
            fixture.Model.Before = null;
            await fixture.SubmitAsync(new VersionSubmission(product.EntityId, VersionId.New(), product.VersionId, product.SchemaVersionId,
                IdentityData.Content(new { name = "Renamed", identifiers = IdentityData.Identifiers(product) })));
        };
        await fixture.RunAsync();
        var renamed = (await fixture.ProductsAsync()).Single();
        await Assert.That(IdentityData.Product(renamed).Name).IsEqualTo("Renamed");
        await Assert.That(IdentityData.Identifiers(renamed).Length).IsEqualTo(2);
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(3);
        await fixture.SubmitAsync(new VersionSubmission(renamed.EntityId, VersionId.New(), renamed.VersionId, renamed.SchemaVersionId,
            IdentityData.Content(new { name = "Renamed", identifiers = IdentityData.Identifiers(renamed).Take(1) })));
        await fixture.RunAsync();
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(4);
    }

    private sealed class FakeModel : IApplicationModel
    {
        public bool IsConfigured { get; set; } = true;
        public List<IdentificationInput> Inputs { get; } = [];
        public Func<IdentificationInput, IdentificationDecision> Decide { get; set; } = _ => new("unknown");
        public Func<IdentificationInput, Task>? Before { get; set; }
        public async Task<IdentificationDecision> IdentifyAsync(IdentificationInput input, CancellationToken token)
        {
            Inputs.Add(input);
            if (Before is not null) await Before(input);
            return Decide(input);
        }
    }

    private sealed class SimulatedProcessExit : Exception;

    private sealed class DeliveringSession(ObserverStore store) : IObserverSession
    {
        private readonly LocalObserverSession local = new(store);
        public string? Crash { get; set; }
        public async Task<JsonElement> RequestAsync(string method, JsonElement parameters, string? requestId = null, CancellationToken cancellationToken = default)
        {
            var result = await local.RequestAsync(method, parameters, requestId ?? Guid.NewGuid().ToString(), cancellationToken);
            if (method == "commit")
            {
                if (Crash == "before") throw new SimulatedProcessExit();
                await store.SendAsync(cancellationToken);
                if (Crash == "after") throw new SimulatedProcessExit();
            }
            return result;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Orisumi.Testing.TestDatabase database;
        private readonly OrisumiApiFactory factory;
        internal FakeModel Model { get; } = new();
        internal List<string> Diagnostics { get; } = [];
        internal string ConnectionString => database.ConnectionString;
        internal IServiceProvider Services => factory.Services;
        internal string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("orisumi-identity-").FullName;
        internal ObserverStore Store { get; }
        internal DeliveringSession Session { get; }
        internal HttpClient Http { get; }
        private VersionSubmission appSchema = null!;
        private VersionSubmission periodSchema = null!;

        private Fixture(Orisumi.Testing.TestDatabase database)
        {
            this.database = database;
            factory = new(database.ConnectionString);
            Http = factory.CreateClient();
            Store = new(Path.Combine(Directory, "store.sqlite"), EntityId.New(), Http);
            Session = new(Store);
        }
        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(await DatabaseFixture.CreateAsync());
            fixture.appSchema = EntityApiTests.Schema("local-applications", """
                {"identifiers":{"type":"array","description":"identifiers","required":true,"nullable":false,"items":{"type":"object","description":"identifier","nullable":false,"fields":{
                "scheme":{"type":"string","description":"scheme","required":true,"nullable":false},"value":{"type":"string","description":"value","required":true,"nullable":false}}}}}
                """);
            fixture.periodSchema = EntityApiTests.Schema("foreground-application-periods", """
                {"localApplicationVersionId":{"type":"reference","description":"application","required":true,"nullable":false},"name":{"type":"string","description":"name","required":true,"nullable":false}}
                """) with { EntityId = IdentityData.PeriodSchemaId };
            await fixture.SubmitAsync(fixture.appSchema, fixture.periodSchema);
            return fixture;
        }
        internal async Task<EntityVersion> ApplicationAsync(string identifier, string path, string name, string scheme = "macos.bundle-id")
        {
            var app = new VersionSubmission(EntityId.New(), VersionId.New(), null, appSchema.VersionId,
                IdentityData.Content(new { identifiers = new[] { new ApplicationIdentifier(scheme, identifier), new("macos.executable-path", path) } }));
            await SubmitAsync(app);
            var saved = await ReadVersionAsync(app.VersionId);
            await PeriodAsync(saved, name);
            return saved;
        }
        internal Task PeriodAsync(EntityVersion app, string name) => SubmitAsync(new VersionSubmission(EntityId.New(), VersionId.New(), null,
            periodSchema.VersionId, IdentityData.Content(new { localApplicationVersionId = app.VersionId.Value, name })));
        internal async Task SubmitAsync(params VersionSubmission[] versions)
        {
            using var response = await Http.PostAsJsonAsync("versions", new { versions = versions.Select(VersionJson.Submission) }, Token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync(Token));
        }
        internal async Task<EntityVersion> ReadVersionAsync(VersionId id) => VersionJson.ReadSaved(
            await Http.GetFromJsonAsync<JsonElement>($"versions/{id.Value}", Token));
        internal Task<List<EntityVersion>> ProductsAsync() => ListAsync("application-products");
        internal async Task<List<EntityVersion>> ListAsync(string? resource = null, string? category = null)
        {
            using var response = await Http.GetAsync($"entities?limit=1000" + (resource is null ? "" : "&resourceName=" + resource) + (category is null ? "" : "&category=" + category), Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return [];
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
            return result.GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSaved).ToList();
        }
        internal async Task RunAsync(string model = "test-model")
        {
            var work = new IdentityWork(Session, Store.InstanceId, Directory);
            await work.ResumeAsync(Token);
            await new IdentityRecognizer(new(Session, work), work, Model, model, "test", message => { Diagnostics.Add(message); Console.WriteLine("DIAGNOSTIC: " + message); }).RunOnceAsync(Token);
        }
        public async ValueTask DisposeAsync()
        {
            Store.Dispose();
            System.IO.Directory.Delete(Directory, true);
            Http.Dispose();
            await factory.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
