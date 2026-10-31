using System.Net;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Orisumi.Observer.Sdk;

namespace Orisumi.Integration.Tests;

public sealed class RuntimeProtocolTests
{
    [Test]
    [Property("caseId", "observer-lifecycle")]
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
