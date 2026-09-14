using Heartbeat.Api.Authentication;
using Heartbeat.Api.Endpoints;
using Heartbeat.Infrastructure;
using Heartbeat.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHeartbeatAuthentication(
    builder.Configuration,
    builder.Environment);
builder.Services.AddProblemDetails();

await using var app = builder.Build();

if (args.Contains("--migrate", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        await app.Services.MigrateDatabaseAsync();
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Database initialization failed ({exception.GetType().Name}). See the database error above.");
        Console.Error.WriteLine("If the Initial migration changed and local data can be discarded, run ./scripts/dev.sh reset, then start again. Reset deletes all local stack data, including the Hub queue.");
        return 1;
    }
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/health/ready", async (HeartbeatDbContext dbContext, CancellationToken cancellationToken) =>
{
    var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
    return canConnect
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.MapCollectorEndpoints();
app.MapTrackEndpoints();
app.MapRecordEndpoints();

await app.RunAsync();
return 0;

public partial class Program;
