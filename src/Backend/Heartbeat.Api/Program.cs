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

var app = builder.Build();

if (args.Contains("--migrate", StringComparer.OrdinalIgnoreCase))
{
    await app.Services.MigrateDatabaseAsync();
    return;
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

await app.RunAsync();

public partial class Program;
