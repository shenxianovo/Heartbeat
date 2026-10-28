using System.Net;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;
using Orisumi.Observers.ForegroundState;

namespace Orisumi.Integration.Tests;

public sealed class RuntimeProtocolTests
{
    [Test]
    [Property("caseId", "sdk-handoff")]
    public async Task LostReceiptReplaysAfterRestartAndDifferentPayloadIsRejected()
    {
        var directory = Directory.CreateTempSubdirectory("orisumi-receipt-");
        try
        {
            var owner = EntityId.New();
            var entity = EntityId.New();
            using var http = new HttpClient(new OfflineHandler()) { BaseAddress = new("http://localhost/") };
            var body = JsonLines.Json(new { operations = new[] { new { op = "draft", entityId = entity.Value,
                category = "entity_schema", content = new { displayName = "Example", description = "Example", resourceName = "examples", fields = new { } }, @as = "version" } } });
            JsonElement receipt;
            var file = Path.Combine(directory.FullName, "state.sqlite");
            using (var sdk = new ObserverClient(file, owner, http))
                receipt = await new LocalObserverSession(sdk).RequestAsync("commit", body, "retry-me");
            using var restarted = new ObserverClient(file, owner, http);
            var session = new LocalObserverSession(restarted);
            var replayed = await session.RequestAsync("commit", body, "retry-me");
            await Assert.That(replayed.GetRawText()).IsEqualTo(receipt.GetRawText());
            await Assert.That(restarted.Execute(local => local.Versions().Count)).IsEqualTo(1);
            var changed = JsonLines.Json(new { operations = new[] { new { op = "complete", entityId = entity.Value } } });
            await Assert.That(async () => await session.RequestAsync("commit", changed, "retry-me")).Throws<InvalidOperationException>();
            await Assert.That(restarted.Execute(local => local.Latest(entity)!.State)).IsEqualTo(SubmissionState.Draft);
        }
        finally { directory.Delete(true); }
    }

    [Test]
    [Property("caseId", "sdk-handoff")]
    public async Task FailedBatchRollsBackReceiptAndVersionsAndReferencesStayFixed()
    {
        var directory = Directory.CreateTempSubdirectory("orisumi-batch-");
        try
        {
            var owner = EntityId.New();
            using var http = new HttpClient(new OfflineHandler()) { BaseAddress = new("http://localhost/") };
            using var sdk = new ObserverClient(Path.Combine(directory.FullName, "state.sqlite"), owner, http);
            var session = new LocalObserverSession(sdk);
            var registration = await ForegroundRegistration.RegisterAsync(session, owner);
            var application = new LocalApplication(EntityId.New(), []);
            var capture = ForegroundCapture.Create(owner, application, ForegroundTimelineTests.Reading(0));
            var store = new ForegroundStore(session, owner, registration);
            store.Checkpoint(capture, null);
            var before = sdk.Execute(local => local.Versions().Count);
            var invalid = JsonLines.Json(new { operations = new object[] {
                new { op = "complete", entityId = capture.ObservationId.Value },
                new { op = "reference", entityId = EntityId.New().Value },
            } });
            await Assert.That(async () => await session.RequestAsync("commit", invalid, "failed")).Throws<InvalidOperationException>();
            await Assert.That(sdk.Execute(local => local.Latest(capture.ObservationId)!.State)).IsEqualTo(SubmissionState.Draft);
            await Assert.That(sdk.Execute(local => local.Versions().Count)).IsEqualTo(before);
            // Failed requests leave no receipt; a corrected request can use the same id.
            await session.RequestAsync("commit", JsonLines.Json(new { operations = new[] { new { op = "complete", entityId = capture.ObservationId.Value } } }), "failed");
            var frozen = sdk.Execute(local => local.Latest(capture.ObservationId)!.Version);
            store.Checkpoint(capture.Through(capture.EndAt.AddSeconds(1)), null);
            var next = sdk.Execute(local => local.Latest(capture.ObservationId)!.Version);
            await Assert.That(next.PreviousVersionId).IsEqualTo(frozen.VersionId);
            await Assert.That(((Observation)next.Content).ContentVersionId).IsEqualTo(((Observation)frozen.Content).ContentVersionId);
            await Assert.That(sdk.Execute(local => local.Find(frozen.VersionId)!.Version)).IsEqualTo(frozen);
        }
        finally { directory.Delete(true); }
    }

    [Test]
    [Property("caseId", "sdk-registration")]
    public async Task HostedRegistrationDoesNotWaitForAnOfflineBackend()
    {
        using var local = new ForegroundTestStore(EntityId.New());
        var session = new LocalObserverSession(local.Sdk);
        var registration = await ForegroundRegistration.RegisterAsync(session, local.Sdk.InstanceId);
        await Assert.That(registration.ObserverSchema).IsEqualTo(local.Registration.ObserverSchema);
        await Assert.That(local.Sdk.Status.Pending).IsGreaterThan(0);
    }

    [Test]
    [Property("caseId", "hosted-sending")]
    public async Task SessionDisposalDoesNotWaitForHostToCloseItsPipe()
    {
        using var host = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var incoming = new AnonymousPipeClientStream(PipeDirection.In, host.GetClientHandleAsString());
        using var writer = new StreamWriter(host) { AutoFlush = true };
        using var outgoing = new StringWriter();
        var session = new PipeObserverSession(TextReader.Synchronized(new StreamReader(incoming)), outgoing);
        await writer.WriteLineAsync("{\"event\":\"init\",\"protocol\":1}");
        await session.Initialization.WaitAsync(TimeSpan.FromSeconds(2));
        await writer.WriteLineAsync("{\"event\":\"stop\"}");
        var disposal = Task.Run(async () => await session.DisposeAsync());
        try { await disposal.WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { writer.Dispose(); }
    }

    [Test]
    [Property("caseId", "observer-lifecycle")]
    public async Task DotnetObserverExitsOnStopWhileRuntimeInputRemainsOpen()
    {
        var fixture = typeof(Orisumi.Observer.Fixture.Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.ChangeExtension(typeof(RuntimeProtocolTests).Assembly.Location, ".runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.ChangeExtension(typeof(RuntimeProtocolTests).Assembly.Location, ".deps.json"));
        start.ArgumentList.Add(fixture);
        using var process = Process.Start(start)!;
        try
        {
            await process.StandardInput.WriteLineAsync("{\"event\":\"init\",\"protocol\":1}");
            await process.StandardInput.FlushAsync();
            var request = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var json = JsonDocument.Parse(request!);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id = json.RootElement.GetProperty("id").GetString(), result = new { } }));
            await process.StandardInput.WriteLineAsync("{\"event\":\"stop\"}");
            await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.That(process.ExitCode).IsEqualTo(0);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
        }
    }

    internal sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
