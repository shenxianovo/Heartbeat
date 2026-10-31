using System.Net.Http.Json;
using System.Text.Json;
using Orisumi.Observer.Sdk;
using Orisumi.Core;
using Orisumi.Testing;
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
        var firstMembership = (await fixture.ListAsync("application-memberships")).Single();
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
        await Assert.That(updated.VersionId).IsEqualTo(first.VersionId);
        await Assert.That(((BusinessContent)updated.Content).Fields.EnumerateObject().Count()).IsEqualTo(1);
        var memberships = await fixture.ListAsync("application-memberships");
        await Assert.That(memberships.Count).IsEqualTo(2);
        var latestMembership = memberships.Single(m => m.EntityId != firstMembership.EntityId);
        var observations = await fixture.ListAsync(category: "observation");
        var latest = observations.Single(o => ((Observation)o.Content).ContentVersionId == latestMembership.VersionId);
        var observation = (Observation)latest.Content;
        await Assert.That(observation.StartAt is null && observation.EndAt is null && observation.TimeZone is null).IsTrue();
        var inputs = (InputSet)(await fixture.ReadVersionAsync(observation.InputSetVersionId!.Value)).Content;
        await Assert.That(inputs.Members.Contains(android.VersionId) && inputs.Members.Contains(first.VersionId)).IsTrue();
        var oldObservation = observations.Single(o => ((Observation)o.Content).ContentVersionId == firstMembership.VersionId);
        var oldInputs = (InputSet)(await fixture.ReadVersionAsync(((Observation)oldObservation.Content).InputSetVersionId!.Value)).Content;
        await Assert.That(oldInputs.Members.Contains(a.VersionId) && oldInputs.Members.Contains(b.VersionId)).IsTrue();
        await Assert.That(inputs.Members.Contains(firstMembership.VersionId)).IsTrue();
        await fixture.ApplicationAsync("unrelated.qq", "/Different/QQ", "QQ");
        fixture.Model.Decide = _ => new("new", Name: "QQ");
        await fixture.RunAsync();
        await Assert.That((await fixture.ProductsAsync()).Count).IsEqualTo(2);
        await Assert.That(fixture.Model.Inputs.Last().Products.Single().EntityId).IsEqualTo(first.EntityId.Value);
    }

    [Test, Property("caseId", "application-identity")]
    public async Task UnknownIsSkippedWhileRunningUntilNamesApplicationsOrTheProcessChange()
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
        fixture.Restart();
        await fixture.RunAsync();
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(4);
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
    public async Task MissingKeyDoesNotIdentifyAndModelChangesVersionTheSameObserver()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ApplicationAsync("com.tencent.qq", "/QQ", "QQ");
        fixture.Model.IsConfigured = false;
        await fixture.RunAsync();
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(0);
        await Assert.That((await fixture.ListAsync("application-identity-observers")).Count).IsEqualTo(1);
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
    public async Task MembershipRetractionRejudgesOnlyThatClueAndProductDeletionVersionsItsMemberships()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ApplicationAsync("a", "/a", "Product");
        await fixture.ApplicationAsync("b", "/b", "Product");
        fixture.Model.Decide = input => input.Products.Length == 0 ? new("new", Name: "Product") : new("existing", input.Products[0].EntityId);
        await fixture.RunAsync();
        var product = (await fixture.ProductsAsync()).Single();
        var before = await fixture.ListAsync("application-memberships");
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(2);
        using (var deleted = await fixture.Http.DeleteAsync("entities/" + before[0].EntityId.Value)) deleted.EnsureSuccessStatusCode();
        await fixture.RunAsync();
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(3);
        var restored = await fixture.ListAsync("application-memberships");
        await Assert.That(restored.Single(m => m.EntityId == before[1].EntityId).VersionId).IsEqualTo(before[1].VersionId);
        using (var deleted = await fixture.Http.DeleteAsync("entities/" + product.EntityId.Value)) deleted.EnsureSuccessStatusCode();
        await fixture.RunAsync();
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(5);
        var after = await fixture.ListAsync("application-memberships");
        foreach (var membership in after) await Assert.That(membership.PreviousVersionId).IsEqualTo(restored.Single(m => m.EntityId == membership.EntityId).VersionId);
    }

    [Test, Property("caseId", "application-identity")]
    public async Task ProductSurvivesMembershipFailureAndIsAvailableToTheNextDecision()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ApplicationAsync("a", "/a", "Product");
        fixture.Model.Decide = input => input.Products.Length == 0 ? new("new", Name: "Product") : new("existing", input.Products[0].EntityId);
        fixture.RejectMembership = true;
        await fixture.RunAsync();
        var product = (await fixture.ProductsAsync()).Single();
        await Assert.That((await fixture.ListAsync("application-memberships")).Count).IsEqualTo(0);
        fixture.RejectMembership = false;
        await fixture.RunAsync();
        await Assert.That((await fixture.ListAsync("application-memberships")).Count).IsEqualTo(1);
        await Assert.That((await fixture.ProductsAsync()).Single().VersionId).IsEqualTo(product.VersionId);
        await Assert.That(fixture.Model.Inputs[1].Products.Single().EntityId).IsEqualTo(product.EntityId.Value);
    }

    private sealed class Network(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal bool RejectMembership { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (RejectMembership && request.RequestUri!.AbsolutePath == "/writes")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                if (body.RootElement.GetProperty("writes").EnumerateArray().Any(w => w.GetProperty("schemaId").GetGuid() == IdentityData.MembershipSchemaId))
                    return new(System.Net.HttpStatusCode.ServiceUnavailable);
            }
            return await base.SendAsync(request, token);
        }
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

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Orisumi.Testing.TestDatabase database;
        private readonly OrisumiApiFactory factory;
        internal FakeModel Model { get; } = new();
        internal List<string> Diagnostics { get; } = [];
        internal string ConnectionString => database.ConnectionString;
        internal string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("orisumi-identity-").FullName;
        internal DataClient Data { get; }
        internal bool RejectMembership { get => network.RejectMembership; set => network.RejectMembership = value; }
        private readonly Network network;
        internal HttpClient Http { get; }
        private IdentityRecognizer? recognizer;
        private string? recognizerModel;
        private EntityId appSchema;
        private WriteClient writer = null!;
        private EntityId periodSchema;

        private Fixture(Orisumi.Testing.TestDatabase database)
        {
            this.database = database;
            factory = new(database.ConnectionString);
            network = new(factory.Server.CreateHandler());
            Http = new HttpClient(network) { BaseAddress = new("http://localhost/") };
            Data = new(Http, Http, Ref.Recognize(IdentityData.ObserverSchemaId.Value, new(null, "test", Guid.NewGuid().ToString())));
        }
        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(await DatabaseFixture.CreateAsync());
            fixture.writer = new WriteClient(fixture.Http); await fixture.writer.InitializeAsync();
            fixture.appSchema = await fixture.writer.DeclareAsync("local-applications", """
                {"identifiers":{"type":"array","description":"identifiers","required":true,"nullable":false,"items":{"type":"object","description":"identifier","nullable":false,"fields":{
                "scheme":{"type":"string","description":"scheme","required":true,"nullable":false},"value":{"type":"string","description":"value","required":true,"nullable":false}}}}}
                """);
            fixture.periodSchema = await fixture.writer.DeclareAsync("foreground-application-periods", """
                {"localApplicationVersionId":{"type":"reference","description":"application","required":true,"nullable":false},"name":{"type":"string","description":"name","required":true,"nullable":false}}
                """, IdentityData.PeriodSchemaId);
            return fixture;
        }
        internal async Task<EntityVersion> ApplicationAsync(string identifier, string path, string name, string scheme = "macos.bundle-id")
        {
            var app = await writer.WriteAsync(appSchema, new { identifiers = new[] { new ApplicationIdentifier(scheme, identifier), new("macos.executable-path", path) } });
            await PeriodAsync(app, name);
            return app;
        }
        internal async Task PeriodAsync(EntityVersion app, string name) => await writer.WriteAsync(periodSchema, new { localApplicationVersionId = app.VersionId.Value, name });
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
        // One recognizer stands for one process; a different model setting starts a new one.
        internal async Task RunAsync(string model = "test-model")
        {
            if (recognizer is null || recognizerModel != model)
            {
                recognizer = new IdentityRecognizer(Data.Direct, new(Data), Model, model, "test", message => { Diagnostics.Add(message); Console.WriteLine("DIAGNOSTIC: " + message); });
                recognizerModel = model;
            }
            await recognizer.RunOnceAsync(Token);
        }
        internal void Restart() => recognizer = null;
        public async ValueTask DisposeAsync()
        {
            System.IO.Directory.Delete(Directory, true);
            Http.Dispose();
            await factory.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
