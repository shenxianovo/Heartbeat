using Orisumi.Application.Entities;
using Orisumi.Application.Observers;
using Orisumi.Observers.ApplicationIdentity;

namespace Orisumi.Api;

internal static class ServerObserverRegistration
{
    public static IServiceCollection AddServerObservers(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddKeyedSingleton<HttpClient>("application-identity", (_, _) => new HttpClient());
        services.AddSingleton<IApplicationModel>(provider => new OpenAiApplicationModel(
            provider.GetRequiredKeyedService<HttpClient>("application-identity"), configuration["OPENAI_API_KEY"],
            configuration["ApplicationIdentity:Model"] ?? "gpt-6.1-terra"));
        services.AddHostedService<ServerObserverHost>();
        return services;
    }
}

internal sealed partial class ServerObserverHost(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<ServerObserverHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(configuration.GetValue("ApplicationIdentity:IntervalSeconds", 60d));
        if (interval <= TimeSpan.Zero || !double.IsFinite(interval.TotalSeconds))
            throw new InvalidOperationException("ApplicationIdentity:IntervalSeconds must be positive and finite.");
        Started(logger);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var services = scope.ServiceProvider;
                    var persistence = services.GetRequiredService<IServerObserverStore>();
                    var id = await persistence.GetOrCreateAsync("application-identity", stoppingToken);
                    var entities = services.GetRequiredService<EntityService>();
                    var session = new ServerObserverSession(id, entities, persistence,
                        status => Delivery(logger, id.Value, status));
                    // Recovery precedes business work, including the API key check and model calls.
                    try { await session.ResumeAsync(stoppingToken); }
                    catch (Orisumi.Core.ModelViolationException exception)
                    { Rejected(logger, exception.Rule, exception.Message); }
                    var recognizer = new IdentityRecognizer(new(entities, session), session, persistence,
                        services.GetRequiredService<IApplicationModel>(), configuration["ApplicationIdentity:Model"] ?? "gpt-6.1-terra",
                        typeof(IdentityRecognizer).Assembly.GetName().Version!.ToString(),
                        message => Diagnostic(logger, message));
                    await recognizer.RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { RoundFailed(logger, exception); }
                // Await the complete round before scheduling another one.
                await Task.Delay(interval, stoppingToken);
            } while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { Stopped(logger); }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server Observer application-identity started")]
    private static partial void Started(ILogger logger);
    [LoggerMessage(Level = LogLevel.Information, Message = "Server Observer application-identity stopped")]
    private static partial void Stopped(ILogger logger);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Server Observer recovery rejected: {Rule} {Detail}")]
    private static partial void Rejected(ILogger logger, string rule, string detail);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Application Identity: {Diagnostic}")]
    private static partial void Diagnostic(ILogger logger, string diagnostic);
    [LoggerMessage(Level = LogLevel.Error, Message = "Server Observer round failed; retrying next round")]
    private static partial void RoundFailed(ILogger logger, Exception exception);
    [LoggerMessage(Level = LogLevel.Information, Message = "Server Observer {InstanceId} delivery: {Status}")]
    private static partial void Delivery(ILogger logger, Guid instanceId, string status);
}
