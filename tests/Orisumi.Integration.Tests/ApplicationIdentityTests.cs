using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task LostSubmissionResponseReplaysTheSameBatchAfterRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ApplicationAsync("com.tencent.qq", "/QQ", "QQ");
        fixture.Model.Decide = _ => new("new", Name: "QQ");
        fixture.Model.Before = _ => { fixture.Handler.LoseNextSubmission = true; return Task.CompletedTask; };
        await fixture.RunAsync();
        var first = (await fixture.ProductsAsync()).Single();
        var pending = new IdentityState(fixture.Directory).Pending;
        await Assert.That(pending is not null).IsTrue();
        await fixture.RunAsync();
        await Assert.That(new IdentityState(fixture.Directory).Pending is null).IsTrue();
        await Assert.That((await fixture.ProductsAsync()).Single().VersionId).IsEqualTo(first.VersionId);
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(1);
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

    private sealed class LossHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool LoseNextSubmission { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (LoseNextSubmission && request.Method == HttpMethod.Post)
            {
                LoseNextSubmission = false;
                response.Dispose();
                throw new HttpRequestException("Response lost after save");
            }
            return response;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Orisumi.Testing.TestDatabase database;
        private readonly OrisumiApiFactory factory;
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "orisumi-identity-" + Guid.NewGuid());
        internal FakeModel Model { get; } = new();
        internal List<string> Diagnostics { get; } = [];
        internal LossHandler Handler { get; }
        internal HttpClient Http { get; }
        private VersionSubmission appSchema = null!;
        private VersionSubmission periodSchema = null!;

        private Fixture(Orisumi.Testing.TestDatabase database)
        {
            this.database = database;
            factory = new(database.ConnectionString);
            Handler = new(factory.Server.CreateHandler());
            Http = new(Handler) { BaseAddress = new Uri("http://localhost/") };
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
        internal Task RunAsync(string model = "test-model")
        {
            var state = new IdentityState(Directory);
            return new IdentityRecognizer(new(Http, state), state, Model, model, "test", Diagnostics.Add).RunOnceAsync(Token);
        }
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await factory.DisposeAsync();
            await database.DisposeAsync();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
}
