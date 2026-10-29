using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using Orisumi.Api;
using Orisumi.Application.Entities;
using Orisumi.Application.Observers;
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
    public async Task ProcessExitDuringSaveReplaysTheSameBatchAfterRestart(bool saved)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ApplicationAsync("com.tencent.qq", "/QQ", "QQ");
        fixture.Model.Decide = _ => new("new", Name: "QQ");
        var persistence = fixture.Persistence;
        var crash = new CrashStore(persistence, saved);
        fixture.Model.Before = _ => { crash.Armed = true; return Task.CompletedTask; };
        await Assert.That(async () => await fixture.RunAsync(persistence: crash)).Throws<SimulatedProcessExit>();
        var id = await persistence.GetOrCreateAsync("application-identity", Token);
        var pending = (await persistence.ReadPendingAsync(id, Token))!;
        using var batch = JsonDocument.Parse(pending);
        var product = batch.RootElement[1].GetProperty("versionId").GetGuid();
        fixture.Model.Before = null;
        await fixture.RunAsync();
        await Assert.That(await persistence.ReadPendingAsync(id, Token)).IsNull();
        await Assert.That((await fixture.ProductsAsync()).Single().VersionId.Value).IsEqualTo(product);
        foreach (var expected in batch.RootElement.EnumerateArray())
        {
            var savedVersion = await fixture.ReadVersionAsync(new(expected.GetProperty("versionId").GetGuid()));
            await Assert.That(JsonElement.DeepEquals(expected, VersionJson.Submission(savedVersion.Submission))).IsTrue();
        }
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(1);
        await Assert.That(await persistence.GetOrCreateAsync("application-identity", Token)).IsEqualTo(id);
    }

    [Test, Property("caseId", "application-identity")]
    public async Task ApiStartupRecoversPendingBeforeCheckingTheModelKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ApplicationAsync("com.tencent.qq", "/QQ", "QQ");
        var crash = new CrashStore(fixture.Persistence, saved: false);
        fixture.Model.Decide = _ => new("new", Name: "QQ");
        fixture.Model.Before = _ => { crash.Armed = true; return Task.CompletedTask; };
        await Assert.That(async () => await fixture.RunAsync(persistence: crash)).Throws<SimulatedProcessExit>();
        fixture.Model.IsConfigured = false;
        var id = await fixture.Persistence.GetOrCreateAsync("application-identity", Token);
        await using var restarted = new OrisumiApiFactory(fixture.ConnectionString,
            configureServices: services => services.AddSingleton<IApplicationModel>(fixture.Model));
        using var http = restarted.CreateClient();
        for (var attempt = 0; attempt < 200 && await fixture.Persistence.ReadPendingAsync(id, Token) is not null; attempt++)
            await Task.Delay(10, Token);
        await Assert.That(await fixture.Persistence.ReadPendingAsync(id, Token)).IsNull();
        await Assert.That((await fixture.ProductsAsync()).Count).IsEqualTo(1);
        await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(1);
    }

    [Test, Property("caseId", "application-identity")]
    public async Task SlowModelDoesNotOverlapRounds()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ApplicationAsync("com.tencent.qq", "/QQ", "QQ");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Model.Before = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(Token); };
        fixture.Model.Decide = _ => new("new", Name: "QQ");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ApplicationIdentity:IntervalSeconds"] = "0.01", ["ApplicationIdentity:Model"] = "test-model" }).Build();
        using var host = new ServerObserverHost(fixture.Services.GetRequiredService<IServiceScopeFactory>(), configuration,
            NullLogger<ServerObserverHost>.Instance);
        await host.StartAsync(Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await Task.Delay(80, Token);
            await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(1);
            release.TrySetResult();
            for (var attempt = 0; attempt < 200 && (await fixture.ProductsAsync()).Count == 0; attempt++)
                await Task.Delay(10, Token);
            await Task.Delay(80, Token);
            await Assert.That((await fixture.ProductsAsync()).Count).IsEqualTo(1);
            await Assert.That(fixture.Model.Inputs.Count).IsEqualTo(1);
        }
        finally { release.TrySetResult(); await host.StopAsync(CancellationToken.None); }
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

    [Test, Property("caseId", "application-identity")]
    public async Task ApiHostsApplicationIdentityWithoutASeparateProcess()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        await Assert.That(factory.Services.GetServices<IHostedService>()
            .Any(service => service.GetType().Name == "ServerObserverHost")).IsTrue();
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

    private sealed class CrashStore(IServerObserverStore inner, bool saved) : IServerObserverStore
    {
        internal bool Armed { get; set; }
        public Task<EntityId> GetOrCreateAsync(string key, CancellationToken token) => inner.GetOrCreateAsync(key, token);
        public Task<string?> ReadPendingAsync(EntityId id, CancellationToken token) => inner.ReadPendingAsync(id, token);
        public Task<string?> ReadStateAsync(EntityId id, CancellationToken token) => inner.ReadStateAsync(id, token);
        public Task SaveStateAsync(EntityId id, string payload, CancellationToken token) => inner.SaveStateAsync(id, payload, token);
        public async Task SavePendingAsync(EntityId id, string payload, CancellationToken token)
        {
            await inner.SavePendingAsync(id, payload, token);
            if (Armed && !saved) throw new SimulatedProcessExit();
        }
        public Task ClearPendingAsync(EntityId id, CancellationToken token) =>
            Armed && saved ? throw new SimulatedProcessExit() : inner.ClearPendingAsync(id, token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Orisumi.Testing.TestDatabase database;
        private readonly OrisumiApiFactory factory;
        internal FakeModel Model { get; } = new();
        internal List<string> Diagnostics { get; } = [];
        internal string ConnectionString => database.ConnectionString;
        internal IServiceProvider Services => factory.Services;
        internal IServerObserverStore Persistence => Services.GetRequiredService<IServerObserverStore>();
        internal HttpClient Http { get; }
        private VersionSubmission appSchema = null!;
        private VersionSubmission periodSchema = null!;

        private Fixture(Orisumi.Testing.TestDatabase database)
        {
            this.database = database;
            factory = new(database.ConnectionString, configureServices: services =>
            {
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType == typeof(ServerObserverHost)).ToArray()) services.Remove(descriptor);
                services.AddSingleton<IApplicationModel>(Model);
            });
            Http = factory.CreateClient();
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
        internal async Task RunAsync(string model = "test-model", IServerObserverStore? persistence = null)
        {
            persistence ??= Persistence;
            await using var scope = Services.CreateAsyncScope();
            var entities = scope.ServiceProvider.GetRequiredService<EntityService>();
            var id = await persistence.GetOrCreateAsync("application-identity", Token);
            var session = new ServerObserverSession(id, entities, persistence);
            await session.ResumeAsync(Token);
            await new IdentityRecognizer(new(entities, session), session, persistence, Model, model, "test", Diagnostics.Add).RunOnceAsync(Token);
        }
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await factory.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
