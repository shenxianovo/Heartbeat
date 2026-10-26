using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Orisumi.Api;

internal static class Telemetry
{
    public static void AddOrisumiTelemetry(this WebApplicationBuilder builder)
    {
        if (!Uri.TryCreate(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"], UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || string.IsNullOrEmpty(endpoint.Host))
        {
            return;
        }

        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.ParseStateValues = true;
        });

        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"];
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                string.IsNullOrWhiteSpace(serviceName) ? builder.Environment.ApplicationName : serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddSource("Npgsql"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation())
            .UseOtlpExporter();
    }
}
