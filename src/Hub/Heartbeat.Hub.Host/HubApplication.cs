using System.Security.Cryptography;
using System.Text;
using Heartbeat.Hub;
using Heartbeat.Hub.Host;
using Microsoft.Data.Sqlite;

using Heartbeat.Hub.Runtime;

namespace Heartbeat.Hub.Host;

public static class HubApplication
{
    public static WebApplication Create(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configureServices?.Invoke(builder.Services);
        builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:4318");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = RecordOutbox.MaximumBatchBytes);
        builder.Services.AddSingleton(services => HubSettings.Read(services.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton(services =>
        {
            var settings = services.GetRequiredService<HubSettings>();
            return new RecordOutbox(services.GetRequiredService<HubLocalStorage>().DatabasePath, settings.Destination, settings.MaximumRecords);
        });
        builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 2_097_152,
        });
        builder.Services.AddSingleton<IBackendTokenProvider>(services =>
        {
            var settings = services.GetRequiredService<HubSettings>();
            return new ApiKeyTokenProvider(
                services.GetRequiredService<HttpClient>(), settings.AuthUrl, settings.ApiKey);
        });
        builder.Services.AddSingleton(services => new RecordUploader(
            services.GetRequiredService<RecordOutbox>(), services.GetRequiredService<HttpClient>(),
            services.GetRequiredService<IBackendTokenProvider>()));
        builder.Services.AddHostedService<UploadWorker>();
        builder.Services.AddSingleton(services => new HubLocalStorage(services.GetRequiredService<HubSettings>().DataDirectory));
        builder.Services.AddSingleton(services => services.GetRequiredService<HubLocalStorage>().Secrets);
        builder.Services.AddSingleton<IHubSubmissionClient>(services => new LocalHubSubmissionClient(services.GetRequiredService<RecordOutbox>()));
        builder.Services.AddSingleton(services => new CollectorManager(
            services.GetRequiredService<HubLocalStorage>(), services.GetServices<ICollectorFactory>()));
        builder.Services.AddSingleton(services => new HubDeliveryLoop(services.GetRequiredService<RecordUploader>(),
            services.GetRequiredService<HubSettings>().UploadInterval));
        builder.Services.AddHostedService<ManagementWorker>();
        builder.Services.AddProblemDetails();
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

        var app = builder.Build();
        var settings = app.Services.GetRequiredService<HubSettings>();
        _ = app.Services.GetRequiredService<RecordOutbox>();
        app.UseExceptionHandler();
        var expectedTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes($"Bearer {settings.AccessToken}"));
        app.Use(async (context, next) =>
        {
            var receivedHash = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
            if (!CryptographicOperations.FixedTimeEquals(expectedTokenHash, receivedHash))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });

        app.MapGet("/hub/v1/status", (RecordOutbox queue) => Results.Ok(queue.Status()));
        app.MapGet("/hub/v1/failures", (RecordOutbox queue) => Results.Ok(queue.ReadFailures()));
        app.MapPost("/hub/v1/records", (HubSubmission submission, RecordOutbox queue) =>
        {
            try
            {
                var accepted = queue.Accept(submission);
                return Results.Ok(new
                {
                    results = accepted.Select((record, index) => new
                    {
                        index,
                        record.Id,
                        status = "accepted",
                        record.EndedAt,
                    }),
                });
            }
            catch (ArgumentException exception)
            {
                return Results.Problem(statusCode: 400, title: "invalid_request", detail: exception.Message);
            }
            catch (RecordConflictException exception)
            {
                return Results.Problem(statusCode: 409, title: "record_conflict", detail: exception.Message);
            }
            catch (Exception exception) when (exception is QueueCapacityException or SqliteException or IOException)
            {
                return Results.Problem(statusCode: 503, title: "custody_unconfirmed",
                    detail: "Persistent acceptance was not confirmed; retain the submitted snapshots and retry.");
            }
        });

        return app;
    }
}
