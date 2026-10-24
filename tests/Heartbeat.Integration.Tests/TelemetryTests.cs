using System.Diagnostics;
using System.Net;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Heartbeat.Integration.Tests;

[NotInParallel]
public sealed partial class TelemetryTests
{
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("localhost:4317")]
    [Arguments("not a URL")]
    [Arguments("file:///tmp/otlp")]
    public async Task WithoutAValidEndpointTheApiRunsWithoutExporters(string? endpoint)
    {
        await using var factory = new HeartbeatApiFactory("Host=localhost;Database=unused", new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint,
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/telemetry-unmapped", TestContext.Current!.Execution.CancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(factory.Services.GetService<TracerProvider>()).IsNull();
        await Assert.That(factory.Services.GetService<MeterProvider>()).IsNull();
        await Assert.That(factory.Services.GetServices<ILoggerProvider>().OfType<OpenTelemetryLoggerProvider>().Any()).IsFalse();
        await Assert.That(factory.Services.GetServices<ILoggerProvider>().Any()).IsTrue();
    }

    [Test]
    public async Task ConfiguredApiExportsStructuredLogsHttpTracesAndMetrics()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var signals = new ConcurrentDictionary<string, int>();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiverBuilder = WebApplication.CreateSlimBuilder();
        receiverBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        receiverBuilder.Logging.ClearProviders();
        await using var receiver = receiverBuilder.Build();
        receiver.MapPost("/v1/{signal}", async (string signal, HttpRequest request) =>
        {
            await request.Body.CopyToAsync(Stream.Null, cancellationToken);
            signals.AddOrUpdate(signal, 1, (_, count) => count + 1);
            if (signals.ContainsKey("logs") && signals.ContainsKey("traces") && signals.ContainsKey("metrics"))
                received.TrySetResult();
            return Results.Ok();
        });
        await receiver.StartAsync(cancellationToken);

        var activities = new List<Activity>();
        var logs = new List<LogRecord>();
        var metrics = new List<Metric>();
        await using var factory = new HeartbeatApiFactory("Host=localhost;Database=unused", new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = receiver.Urls.Single(),
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_SERVICE_NAME"] = "heartbeat-telemetry-test",
        });
        using var instrumented = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddInMemoryExporter(activities))
                .WithMetrics(meter => meter.AddInMemoryExporter(metrics))
                .WithLogging(logging => logging.AddInMemoryExporter(logs));
        }));
        using var client = instrumented.CreateClient();
        using var response = await client.GetAsync("/telemetry-unmapped", cancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var logger = instrumented.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TelemetryVerification");
        LogVerification(logger, 42);

        await Assert.That(instrumented.Services.GetRequiredService<TracerProvider>().ForceFlush()).IsTrue();
        await Assert.That(instrumented.Services.GetRequiredService<MeterProvider>().ForceFlush()).IsTrue();
        var loggerProvider = instrumented.Services.GetRequiredService<LoggerProvider>();
        await Assert.That(loggerProvider.ForceFlush()).IsTrue();

        await Assert.That(activities.Any(activity => activity.Kind == ActivityKind.Server
            && activity.GetTagItem("url.path") as string == "/telemetry-unmapped")).IsTrue();
        var log = logs.Single(record => record.CategoryName == "TelemetryVerification");
        await Assert.That(log.FormattedMessage).IsEqualTo("Telemetry verification 42");
        await Assert.That(log.Attributes!.Any(attribute => attribute.Key == "VerificationCode" && Equals(attribute.Value, 42))).IsTrue();
        await Assert.That(metrics.Any(metric => metric.Name == "http.server.request.duration")).IsTrue();
        await Assert.That(metrics.Any(metric => metric.Name.StartsWith("dotnet.", StringComparison.Ordinal))).IsTrue();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }
    [LoggerMessage(Level = LogLevel.Information, Message = "Telemetry verification {VerificationCode}")]
    private static partial void LogVerification(ILogger logger, int verificationCode);
}
